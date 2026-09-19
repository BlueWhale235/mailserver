<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ApiError, api, apiDownload, jsonBody } from '../../lib/api'
import PageHeader from '../../components/PageHeader.vue'
import StatusPill from '../../components/StatusPill.vue'

type AuditCategory = 'Security' | 'Create' | 'Update' | 'Delete' | 'Plugin' | 'BackgroundTask' | 'Other'
type AuditStatus = 'Running' | 'Succeeded' | 'Failed' | 'Warning'

interface AuditListItem {
  id: string
  number: string
  content: string
  category: AuditCategory
  status: AuditStatus
  actor: string
  requestId?: string
  pluginId?: string
  pluginName?: string
  createdAt: string
}

interface AuditFieldChange {
  field: string
  oldValue?: string
  newValue?: string
}

interface AuditDetail extends AuditListItem {
  action: string
  resource: string
  detail?: string
  remoteAddress?: string
  relatedId?: string
  durationMilliseconds?: number
  changes: AuditFieldChange[]
  rawJson?: string
}

interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

interface AuditFilters {
  search: string
  category: '' | AuditCategory
  status: '' | AuditStatus
  from: string
  to: string
}

const emptyFilters = (): AuditFilters => ({ search: '', category: '', status: '', from: '', to: '' })

const rows = ref<AuditListItem[]>([])
const totalCount = ref(0)
const page = ref(1)
const pageSize = 20
const loading = ref(false)
const error = ref('')
const notice = ref('')
const filterError = ref('')
const search = ref<string | null>('')
const category = ref<'' | AuditCategory>('')
const status = ref<'' | AuditStatus>('')
const from = ref('')
const to = ref('')
const appliedFilters = ref<AuditFilters>(emptyFilters())
const selectedIds = ref<string[]>([])
const exportLoading = ref(false)

const detailOpen = ref(false)
const detailLoading = ref(false)
const detailError = ref('')
const detail = ref<AuditDetail | null>(null)
const copyState = ref<'idle' | 'copied' | 'failed'>('idle')

const categoryItems = [
  { title: '全部类别', value: '' },
  { title: '安全事件', value: 'Security' },
  { title: '创建', value: 'Create' },
  { title: '更新', value: 'Update' },
  { title: '删除', value: 'Delete' },
  { title: '插件', value: 'Plugin' },
  { title: '后台任务', value: 'BackgroundTask' },
  { title: '其他', value: 'Other' },
]

const statusItems = [
  { title: '全部结果', value: '' },
  { title: '运行中', value: 'Running' },
  { title: '成功', value: 'Succeeded' },
  { title: '失败', value: 'Failed' },
  { title: '警告', value: 'Warning' },
]

const totalPages = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))
const visibleIds = computed(() => rows.value.map(row => row.id))
const selectedCount = computed(() => selectedIds.value.length)
const allVisibleSelected = computed(() => visibleIds.value.length > 0 && visibleIds.value.every(id => selectedIds.value.includes(id)))
const someVisibleSelected = computed(() => !allVisibleSelected.value && visibleIds.value.some(id => selectedIds.value.includes(id)))
const activeFilterCount = computed(() => Object.values(appliedFilters.value).filter(Boolean).length)
const filtersChanged = computed(() => JSON.stringify(draftFilters()) !== JSON.stringify(appliedFilters.value))

function draftFilters(): AuditFilters {
  return {
    search: search.value?.trim() ?? '',
    category: category.value,
    status: status.value,
    from: from.value,
    to: to.value,
  }
}

function dateIsValid(value: string) {
  return !value || Number.isFinite(new Date(value).getTime())
}

async function loadAudit() {
  loading.value = true
  error.value = ''
  try {
    const params = new globalThis.URLSearchParams({ page: String(page.value), pageSize: String(pageSize) })
    const filters = appliedFilters.value
    if (filters.search) params.set('search', filters.search)
    if (filters.category) params.set('category', filters.category)
    if (filters.status) params.set('status', filters.status)
    if (filters.from) params.set('from', new Date(filters.from).toISOString())
    if (filters.to) params.set('to', new Date(filters.to).toISOString())
    const result = await api<PagedResult<AuditListItem>>(`/audit?${params.toString()}`)
    rows.value = result.items
    totalCount.value = result.totalCount
  } catch (cause) {
    rows.value = []
    totalCount.value = 0
    error.value = cause instanceof Error ? cause.message : '审计日志加载失败'
  } finally {
    loading.value = false
  }
}

function applyFilters() {
  filterError.value = ''
  const next = draftFilters()
  if (!dateIsValid(next.from) || !dateIsValid(next.to)) {
    filterError.value = '请输入有效的时间范围。'
    return
  }
  if (next.from && next.to && new Date(next.from) > new Date(next.to)) {
    filterError.value = '开始时间不能晚于结束时间。'
    return
  }
  appliedFilters.value = next
  selectedIds.value = []
  page.value = 1
  void loadAudit()
}

function resetFilters() {
  const empty = emptyFilters()
  search.value = empty.search
  category.value = empty.category
  status.value = empty.status
  from.value = empty.from
  to.value = empty.to
  appliedFilters.value = empty
  filterError.value = ''
  selectedIds.value = []
  page.value = 1
  void loadAudit()
}

function toggleSelection(id: string, checked: boolean | null) {
  selectedIds.value = checked
    ? Array.from(new Set([...selectedIds.value, id]))
    : selectedIds.value.filter(item => item !== id)
}

function togglePageSelection(checked: boolean | null) {
  const current = new Set(selectedIds.value)
  visibleIds.value.forEach(id => checked ? current.add(id) : current.delete(id))
  selectedIds.value = Array.from(current)
}

async function exportSelected() {
  if (!selectedIds.value.length) return
  exportLoading.value = true
  error.value = ''
  try {
    const result = await apiDownload('/audit/export', {
      method: 'POST',
      body: jsonBody({ ids: selectedIds.value }),
    })
    const href = globalThis.URL.createObjectURL(result.blob)
    const anchor = globalThis.document.createElement('a')
    anchor.href = href
    anchor.download = result.fileName
    anchor.click()
    globalThis.URL.revokeObjectURL(href)
    notice.value = `已导出 ${selectedIds.value.length} 条审计记录：${result.fileName}`
  } catch (cause) {
    error.value = cause instanceof ApiError || cause instanceof Error ? cause.message : '审计日志导出失败'
  } finally {
    exportLoading.value = false
  }
}

async function openDetail(row: AuditListItem) {
  detailOpen.value = true
  detailLoading.value = true
  detailError.value = ''
  detail.value = null
  copyState.value = 'idle'
  try {
    detail.value = await api<AuditDetail>(`/audit/${row.id}`)
  } catch (cause) {
    detailError.value = cause instanceof Error ? cause.message : '审计详情加载失败'
  } finally {
    detailLoading.value = false
  }
}

async function copyRawJson() {
  if (!detail.value?.rawJson) return
  try {
    await globalThis.navigator.clipboard.writeText(detail.value.rawJson)
    copyState.value = 'copied'
  } catch {
    copyState.value = 'failed'
  }
}

function categoryLabel(value: AuditCategory) {
  return categoryItems.find(item => item.value === value)?.title ?? value
}

function categoryIcon(value: AuditCategory) {
  const icons: Record<AuditCategory, string> = {
    Security: 'mdi-shield-alert-outline',
    Create: 'mdi-plus-circle-outline',
    Update: 'mdi-pencil-outline',
    Delete: 'mdi-delete-outline',
    Plugin: 'mdi-puzzle-outline',
    BackgroundTask: 'mdi-calendar-clock-outline',
    Other: 'mdi-dots-horizontal-circle-outline',
  }
  return icons[value]
}

function statusView(value: AuditStatus) {
  if (value === 'Succeeded') return { label: '成功', tone: 'success' as const }
  if (value === 'Failed') return { label: '失败', tone: 'danger' as const }
  if (value === 'Warning') return { label: '警告', tone: 'warning' as const }
  return { label: '运行中', tone: 'neutral' as const }
}

function formatDate(value?: string) {
  return value ? new Date(value).toLocaleString('zh-CN') : '—'
}

function formatDuration(value?: number) {
  if (value === undefined || value === null) return '—'
  if (value < 1000) return `${value} ms`
  return `${(value / 1000).toFixed(value < 10_000 ? 1 : 0)} s`
}

function changeValue(value?: string) {
  return value === undefined || value === null || value === '' ? '（空）' : value
}

onMounted(loadAudit)
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/system">系统</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>审计日志</span>
  </div>

  <PageHeader title="审计日志" description="追踪管理操作、安全事件与后台任务">
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="loading" @click="loadAudit">刷新</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>

  <v-card flat class="panel filter-panel mb-4">
    <div class="filter-title">
      <div><h2>筛选条件</h2><p>组合条件后统一查询，避免输入过程中反复刷新列表</p></div>
      <v-chip v-if="activeFilterCount" size="small" color="primary" variant="tonal">已应用 {{ activeFilterCount }} 项</v-chip>
    </div>
    <div class="filter-grid">
      <v-text-field v-model="search" label="关键词" placeholder="编号、操作人、动作或资源" prepend-inner-icon="mdi-magnify" clearable hide-details density="compact" @keyup.enter="applyFilters" />
      <v-select v-model="category" :items="categoryItems" label="事件类别" hide-details density="compact" />
      <v-select v-model="status" :items="statusItems" label="执行结果" hide-details density="compact" />
      <v-text-field v-model="from" label="开始时间" type="datetime-local" hide-details density="compact" />
      <v-text-field v-model="to" label="结束时间" type="datetime-local" hide-details density="compact" />
      <div class="filter-actions">
        <v-btn variant="text" :disabled="!activeFilterCount && !filtersChanged" @click="resetFilters">重置</v-btn>
        <v-btn color="primary" prepend-icon="mdi-filter-outline" :disabled="!filtersChanged" @click="applyFilters">应用筛选</v-btn>
      </div>
    </div>
    <v-alert v-if="filterError" type="warning" variant="tonal" density="compact" class="mt-3">{{ filterError }}</v-alert>
  </v-card>

  <v-card flat class="panel table-panel">
    <div class="audit-toolbar">
      <div>
        <strong>{{ selectedCount ? `已选择 ${selectedCount} 条` : '审计事件' }}</strong>
        <span>{{ selectedCount ? '仅导出选中的完整脱敏记录' : `共 ${totalCount} 条记录` }}</span>
      </div>
      <v-btn
        color="primary"
        variant="tonal"
        prepend-icon="mdi-download-outline"
        :loading="exportLoading"
        :disabled="!selectedCount"
        @click="exportSelected"
      >导出所选</v-btn>
    </div>
    <v-table>
      <thead>
        <tr>
          <th class="select-cell"><v-checkbox-btn :model-value="allVisibleSelected" :indeterminate="someVisibleSelected" aria-label="选择当前页" @update:model-value="togglePageSelection" /></th>
          <th>事件</th><th>类别</th><th>结果</th><th>操作人</th><th>时间</th><th></th>
        </tr>
      </thead>
      <tbody>
        <tr v-if="loading"><td colspan="7"><v-progress-linear indeterminate color="primary" /></td></tr>
        <tr v-else-if="!rows.length"><td colspan="7" class="empty-state"><v-icon size="32">mdi-text-box-search-outline</v-icon><b>没有符合条件的审计事件</b><span>调整筛选条件后重试</span></td></tr>
        <tr v-for="row in rows" v-else :key="row.id" :class="{ 'is-selected': selectedIds.includes(row.id) }">
          <td class="select-cell"><v-checkbox-btn :model-value="selectedIds.includes(row.id)" :aria-label="`选择审计事件 ${row.number}`" @update:model-value="value => toggleSelection(row.id, value)" /></td>
          <td><b>{{ row.content }}</b><small><code>{{ row.number }}</code><span v-if="row.requestId"> · 请求 {{ row.requestId }}</span></small></td>
          <td><span class="category"><v-icon size="16">{{ categoryIcon(row.category) }}</v-icon>{{ categoryLabel(row.category) }}</span></td>
          <td><StatusPill v-bind="statusView(row.status)" /></td>
          <td>{{ row.actor }}</td>
          <td>{{ formatDate(row.createdAt) }}</td>
          <td class="text-end"><v-btn size="small" variant="text" icon="mdi-text-box-search-outline" :aria-label="`查看审计事件 ${row.number} 详情`" @click="openDetail(row)" /></td>
        </tr>
      </tbody>
    </v-table>
    <div class="table-footer">
      <span>第 {{ page }} / {{ totalPages }} 页 · 共 {{ totalCount }} 条</span>
      <v-pagination v-model="page" :length="totalPages" density="compact" @update:model-value="loadAudit" />
    </div>
  </v-card>

  <v-dialog v-model="detailOpen" max-width="860">
    <v-card>
      <v-card-title class="detail-title">
        <span><v-icon color="primary">mdi-text-box-search-outline</v-icon> 审计事件详情</span>
        <v-btn icon="mdi-close" variant="text" aria-label="关闭审计详情" @click="detailOpen = false" />
      </v-card-title>
      <v-card-text>
        <v-progress-linear v-if="detailLoading" indeterminate color="primary" />
        <v-alert v-else-if="detailError" type="error" variant="tonal">{{ detailError }}</v-alert>
        <template v-else-if="detail">
          <div class="detail-lead">
            <div><span>{{ detail.number }}</span><h2>{{ detail.content }}</h2></div>
            <StatusPill v-bind="statusView(detail.status)" />
          </div>
          <v-alert v-if="detail.detail" :type="detail.status === 'Succeeded' ? 'info' : 'warning'" variant="tonal" density="compact" class="mb-5">{{ detail.detail }}</v-alert>

          <dl class="detail-grid">
            <div><dt>类别</dt><dd>{{ categoryLabel(detail.category) }}</dd></div>
            <div><dt>发生时间</dt><dd>{{ formatDate(detail.createdAt) }}</dd></div>
            <div><dt>操作人</dt><dd>{{ detail.actor }}</dd></div>
            <div><dt>来源地址</dt><dd><code>{{ detail.remoteAddress || '—' }}</code></dd></div>
            <div><dt>动作</dt><dd><code>{{ detail.action }}</code></dd></div>
            <div><dt>资源</dt><dd><code>{{ detail.resource }}</code></dd></div>
            <div><dt>耗时</dt><dd>{{ formatDuration(detail.durationMilliseconds) }}</dd></div>
            <div><dt>请求 ID</dt><dd><code>{{ detail.requestId || '—' }}</code></dd></div>
            <div><dt>关联 ID</dt><dd><code>{{ detail.relatedId || '—' }}</code></dd></div>
            <div v-if="detail.pluginName || detail.pluginId"><dt>插件</dt><dd>{{ detail.pluginName || detail.pluginId }}</dd></div>
          </dl>

          <section class="changes-section">
            <h3>字段变更 <span>{{ detail.changes?.length ?? 0 }}</span></h3>
            <div v-if="detail.changes?.length" class="change-table">
              <div class="change-head"><span>字段</span><span>修改前</span><span>修改后</span></div>
              <div v-for="change in detail.changes" :key="`${change.field}-${change.oldValue}-${change.newValue}`" class="change-row">
                <code>{{ change.field }}</code><span>{{ changeValue(change.oldValue) }}</span><b>{{ changeValue(change.newValue) }}</b>
              </div>
            </div>
            <p v-else class="no-changes">该事件没有记录字段级变更。</p>
          </section>

          <v-expansion-panels v-if="detail.rawJson" variant="accordion" class="raw-panel">
            <v-expansion-panel>
              <v-expansion-panel-title>已脱敏原始记录</v-expansion-panel-title>
              <v-expansion-panel-text>
                <div class="raw-actions">
                  <span>敏感字段在服务端写入审计记录前已隐藏。</span>
                  <v-btn size="small" variant="text" prepend-icon="mdi-content-copy" @click="copyRawJson">复制 JSON</v-btn>
                </div>
                <v-alert v-if="copyState === 'copied'" type="success" density="compact" variant="tonal" class="mb-3">JSON 已复制到剪贴板。</v-alert>
                <v-alert v-if="copyState === 'failed'" type="warning" density="compact" variant="tonal" class="mb-3">浏览器未允许访问剪贴板，请手动选择复制。</v-alert>
                <pre>{{ detail.rawJson }}</pre>
              </v-expansion-panel-text>
            </v-expansion-panel>
          </v-expansion-panels>
        </template>
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn color="primary" @click="detailOpen = false">关闭</v-btn></v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.filter-panel { padding: 18px 20px 20px; }
.filter-title { display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; margin-bottom: 16px; }
.filter-title h2 { margin: 0 0 3px; font-size: 16px; }
.filter-title p { margin: 0; color: var(--text-muted); font-size: 12px; }
.filter-grid { display: grid; grid-template-columns: minmax(240px, 1.4fr) 160px 140px minmax(190px, 1fr) minmax(190px, 1fr) auto; align-items: center; gap: 10px; }
.filter-actions { display: flex; align-items: center; justify-content: flex-end; gap: 6px; white-space: nowrap; }
.audit-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 20px; min-height: 65px; padding: 12px 20px; border-bottom: 1px solid var(--border); }
.audit-toolbar > div { display: flex; flex-direction: column; gap: 2px; }
.audit-toolbar strong { color: var(--text); font-size: 14px; }
.audit-toolbar span { color: var(--text-muted); font-size: 12px; }
.select-cell { width: 54px; padding-right: 0 !important; }
.category { display: inline-flex; align-items: center; gap: 6px; color: var(--text-secondary); white-space: nowrap; }
.is-selected { background: #f5f9ff; }
.empty-state { height: 190px; color: var(--text-muted); text-align: center; }
.empty-state :deep(.v-icon), .empty-state b, .empty-state span { display: block; margin: 5px auto; }
.detail-title { display: flex; align-items: center; justify-content: space-between; padding-right: 12px; }
.detail-title > span { display: inline-flex; align-items: center; gap: 8px; }
.detail-lead { display: flex; align-items: flex-start; justify-content: space-between; gap: 24px; margin-bottom: 16px; }
.detail-lead span { color: var(--text-muted); font-family: monospace; font-size: 12px; }
.detail-lead h2 { margin: 3px 0 0; color: var(--text); font-size: 19px; }
.detail-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 0; margin: 0; overflow: hidden; border: 1px solid var(--border); border-radius: 8px; }
.detail-grid div { min-width: 0; padding: 11px 14px; border-right: 1px solid var(--border); border-bottom: 1px solid var(--border); }
.detail-grid div:nth-child(2n) { border-right: 0; }
.detail-grid dt { color: var(--text-muted); font-size: 11px; }
.detail-grid dd { margin: 4px 0 0; overflow-wrap: anywhere; color: var(--text); font-size: 13px; font-weight: 600; }
.changes-section { margin-top: 22px; }
.changes-section h3 { margin: 0 0 10px; font-size: 14px; }
.changes-section h3 span { margin-left: 6px; color: var(--text-muted); font-size: 12px; font-weight: 500; }
.change-table { overflow: hidden; border: 1px solid var(--border); border-radius: 8px; }
.change-head, .change-row { display: grid; grid-template-columns: 160px 1fr 1fr; gap: 14px; padding: 9px 13px; border-bottom: 1px solid var(--border); }
.change-head { color: var(--text-muted); background: #f8fafc; font-size: 11px; font-weight: 700; }
.change-row:last-child { border-bottom: 0; }
.change-row span, .change-row b { overflow-wrap: anywhere; font-size: 12px; }
.no-changes { margin: 0; padding: 14px; border: 1px dashed var(--border); border-radius: 8px; color: var(--text-muted); font-size: 12px; }
.raw-panel { margin-top: 18px; }
.raw-actions { display: flex; align-items: center; justify-content: space-between; gap: 18px; margin-bottom: 10px; color: var(--text-muted); font-size: 12px; }
pre { max-height: 260px; margin: 0; padding: 14px; overflow: auto; border-radius: 7px; color: #d8e4f0; background: #142234; font-size: 12px; line-height: 1.6; white-space: pre-wrap; overflow-wrap: anywhere; }
@media (max-width: 1580px) {
  .filter-grid { grid-template-columns: minmax(260px, 1fr) minmax(180px, .55fr) minmax(200px, .6fr); }
  .filter-actions { justify-content: flex-start; }
}
@media (max-width: 760px) {
  .filter-grid, .detail-grid { grid-template-columns: 1fr; }
  .filter-actions, .audit-toolbar, .detail-lead { align-items: stretch; flex-direction: column; }
  .detail-grid div { border-right: 0; }
  .change-head, .change-row { grid-template-columns: 1fr; }
}
</style>
