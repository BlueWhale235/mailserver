<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import PageHeader from '../../components/PageHeader.vue'
import StatusPill from '../../components/StatusPill.vue'
import { usePermission } from '../../composables/usePermission'
import { ApiError, api } from '../../lib/api'

type ResourceType = 'Domain' | 'Account' | 'Mailbox' | 'MailboxMessage' | 'ContentFile'
type QueueStatus = 'Pending' | 'Failed'

interface RecycleBinItem {
  queueId: string
  resourceType: ResourceType
  resourceId: string | null
  displayName: string
  sender?: string | null
  subject?: string | null
  receivedAt?: string | null
  estimatedBytes: number
  deletedAt: string
  purgeAfter: string
  status: QueueStatus
}

const resourceTypes: Array<{ title: string; value: '' | ResourceType }> = [
  { title: '全部类型', value: '' },
  { title: '域', value: 'Domain' },
  { title: '账号', value: 'Account' },
  { title: '邮箱文件夹', value: 'Mailbox' },
  { title: '邮件', value: 'MailboxMessage' },
  { title: '内容文件', value: 'ContentFile' },
]
const statusTypes: Array<{ title: string; value: '' | QueueStatus }> = [
  { title: '全部状态', value: '' },
  { title: '等待清理', value: 'Pending' },
  { title: '清理失败', value: 'Failed' },
]

const { can } = usePermission()
const items = ref<RecycleBinItem[]>([])
const loading = ref(false)
const actionLoading = ref(false)
const error = ref('')
const notice = ref('')
const search = ref<string | null>('')
const resourceType = ref<'' | ResourceType>('')
const status = ref<'' | QueueStatus>('')
const target = ref<RecycleBinItem | null>(null)
const selectedIds = ref<string[]>([])
const bulkPurgeOpen = ref(false)
const action = ref<'restore' | 'purge'>('restore')
const confirmation = ref('')
const dialogError = ref('')
const page = ref(1)
const pageSize = 20
const totalCount = ref(0)
const totalPages = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))
const appliedFilters = ref({ search: '', resourceType: '' as '' | ResourceType, status: '' as '' | QueueStatus })
const filtersChanged = computed(() => (search.value?.trim() ?? '') !== appliedFilters.value.search
  || resourceType.value !== appliedFilters.value.resourceType || status.value !== appliedFilters.value.status)
let requestVersion = 0

const totalBytes = computed(() => items.value.reduce((sum, item) => sum + item.estimatedBytes, 0))
const failedCount = computed(() => items.value.filter(item => item.status === 'Failed').length)
const urgentCount = computed(() => items.value.filter(item => new Date(item.purgeAfter).getTime() - Date.now() <= 72 * 60 * 60 * 1000).length)
const activeFilterCount = computed(() => Number(Boolean(appliedFilters.value.search)) + Number(Boolean(appliedFilters.value.resourceType)) + Number(Boolean(appliedFilters.value.status)))
const canRestoreTarget = computed(() => target.value ? isRestorable(target.value) : false)
const selectedItems = computed(() => items.value.filter(item => selectedIds.value.includes(item.queueId)))
const allPageSelected = computed(() => items.value.length > 0 && selectedItems.value.length === items.value.length)
const canExecute = computed(() => {
  if (actionLoading.value) return false
  if (bulkPurgeOpen.value) return selectedItems.value.length > 0 && confirmation.value === '永久清理'
  if (!target.value) return false
  return action.value === 'restore' ? canRestoreTarget.value : confirmation.value === displayName(target.value)
})

async function load() {
  const request = ++requestVersion
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSize) })
    if (appliedFilters.value.search) params.set('search', appliedFilters.value.search)
    if (appliedFilters.value.resourceType) params.set('resourceType', appliedFilters.value.resourceType)
    if (appliedFilters.value.status) params.set('status', appliedFilters.value.status)
    const result = await api<{ items: RecycleBinItem[]; totalCount: number }>(`/recycle-bin/search?${params}`)
    if (request !== requestVersion) return
    items.value = result.items
    selectedIds.value = selectedIds.value.filter(id => result.items.some(item => item.queueId === id))
    totalCount.value = result.totalCount
    if (page.value > totalPages.value) {
      page.value = totalPages.value
      await load()
    }
  } catch (cause) {
    if (request !== requestVersion) return
    items.value = []
    selectedIds.value = []
    totalCount.value = 0
    error.value = cause instanceof Error ? cause.message : '回收站加载失败'
  } finally {
    if (request === requestVersion) loading.value = false
  }
}

function resetFilters() {
  selectedIds.value = []
  search.value = ''
  resourceType.value = ''
  status.value = ''
  appliedFilters.value = { search: '', resourceType: '', status: '' }
  page.value = 1
  void load()
}

function applyFilters() {
  selectedIds.value = []
  appliedFilters.value = { search: search.value?.trim() ?? '', resourceType: resourceType.value, status: status.value }
  page.value = 1
  void load()
}

function changePage(next: number) {
  if (next < 1 || next > totalPages.value || next === page.value) return
  selectedIds.value = []
  page.value = next
  void load()
}

function openAction(item: RecycleBinItem, operation: 'restore' | 'purge') {
  if (operation === 'restore' && !isRestorable(item)) return
  target.value = item
  action.value = operation
  confirmation.value = ''
  dialogError.value = ''
}

function openBulkPurge() {
  if (!selectedItems.value.length) return
  bulkPurgeOpen.value = true
  confirmation.value = ''
  dialogError.value = ''
}

function toggleAllPage() {
  selectedIds.value = allPageSelected.value ? [] : items.value.map(item => item.queueId)
}

function closeDialog() {
  if (actionLoading.value) return
  target.value = null
  bulkPurgeOpen.value = false
  confirmation.value = ''
  dialogError.value = ''
}

async function execute() {
  if (!canExecute.value) return
  if (bulkPurgeOpen.value) {
    actionLoading.value = true
    dialogError.value = ''
    try {
      const result = await api<{ results: Array<{ id: string; status: string; error: string | null }> }>('/recycle-bin/batch/purge', {
        method: 'POST', body: JSON.stringify({ ids: selectedItems.value.map(item => item.queueId) }),
      })
      const removed = result.results.filter(item => item.status === 'deleted').length
      const failed = result.results.filter(item => item.status !== 'deleted')
      bulkPurgeOpen.value = false
      confirmation.value = ''
      notice.value = removed ? `已永久清理 ${removed} 个对象。` : ''
      await load()
      selectedIds.value = failed.map(item => item.id).filter(id => items.value.some(item => item.queueId === id))
      if (failed.length) error.value = `${failed.length} 个对象未清理：${failed.map(item => item.error ?? item.status).join('；')}`
    } catch (cause) {
      dialogError.value = cause instanceof Error ? cause.message : '批量清理失败，请稍后重试'
    } finally {
      actionLoading.value = false
    }
    return
  }
  if (!target.value) return
  actionLoading.value = true
  dialogError.value = ''
  const currentTarget = target.value
  try {
    await api(`/recycle-bin/${currentTarget.queueId}${action.value === 'restore' ? '/restore' : ''}`, {
      method: action.value === 'restore' ? 'POST' : 'DELETE',
    })
    notice.value = action.value === 'restore'
      ? `“${displayName(currentTarget)}”已恢复。`
      : `“${displayName(currentTarget)}”已永久清理。`
    target.value = null
    await load()
  } catch (cause) {
    dialogError.value = cause instanceof ApiError && cause.status === 409
      ? `${cause.message} 请刷新列表核对状态后再操作。`
      : cause instanceof Error ? cause.message : '操作失败，请稍后重试'
  } finally {
    actionLoading.value = false
  }
}

function isRestorable(item: RecycleBinItem) {
  return item.resourceType !== 'ContentFile' && Boolean(item.resourceId)
    && new Date(item.purgeAfter).getTime() > Date.now()
}

function restoreUnavailableReason(item: RecycleBinItem) {
  if (new Date(item.purgeAfter).getTime() <= Date.now()) return '保留期已结束，无法恢复'
  return '内容文件没有可恢复的数据库对象'
}

function typeView(value: ResourceType) {
  const views: Record<ResourceType, { label: string; icon: string }> = {
    Domain: { label: '域', icon: 'mdi-web' },
    Account: { label: '账号', icon: 'mdi-account-outline' },
    Mailbox: { label: '邮箱文件夹', icon: 'mdi-folder-outline' },
    MailboxMessage: { label: '邮件', icon: 'mdi-email-outline' },
    ContentFile: { label: '内容文件', icon: 'mdi-file-outline' },
  }
  return views[value]
}

function statusView(value: QueueStatus) {
  return value === 'Failed'
    ? { label: '清理失败', tone: 'danger' as const }
    : { label: '等待清理', tone: 'neutral' as const }
}

function formatDate(value: string) {
  return new Date(value).toLocaleString('zh-CN', { hour12: false })
}

function displayName(item: RecycleBinItem) {
  if (item.resourceType !== 'MailboxMessage' || !item.receivedAt) return item.displayName
  return `${item.sender || '（未知发件人）'} · ${item.subject || '（无主题）'} · ${formatDate(item.receivedAt)}`
}

function formatBytes(value: number) {
  if (value <= 0) return '0 B'
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB']
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1)
  const scaled = value / 1024 ** index
  return `${scaled.toFixed(scaled >= 10 || index === 0 ? 0 : 1)} ${units[index]}`
}

function purgeWindow(value: string) {
  const delta = new Date(value).getTime() - Date.now()
  if (delta <= 0) return { label: '已到清理时间', urgent: true }
  const hours = Math.ceil(delta / 3_600_000)
  if (hours < 24) return { label: `${hours} 小时后`, urgent: true }
  const days = Math.ceil(hours / 24)
  return { label: `${days} 天后`, urgent: days <= 3 }
}

function shortId(value: string) {
  return value.slice(0, 8)
}

onMounted(load)
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/system">系统</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>回收站</span>
  </div>

  <PageHeader title="回收站" description="审阅软删除对象，在保留期内恢复，或确认后永久清理">
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="loading" @click="load">刷新</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>

  <section v-if="!error" class="recycle-summary" aria-label="回收站摘要">
    <div><span>符合条件的对象</span><strong>{{ totalCount }}</strong><small>服务端筛选结果</small></div>
    <div><span>本页预计可释放</span><strong>{{ formatBytes(totalBytes) }}</strong><small>最终释放量以清理结果为准</small></div>
    <div><span>本页清理失败</span><strong :class="{ danger: failedCount }">{{ failedCount }}</strong><small>需要检查任务与存储状态</small></div>
    <div><span>本页即将或已经到期</span><strong :class="{ warning: urgentCount }">{{ urgentCount }}</strong><small>72 小时内进入自动清理</small></div>
  </section>

  <v-alert type="warning" variant="tonal" density="compact" class="recycle-warning">
    到达计划清理时间后，维护任务会永久删除对象。永久清理无法撤销，关联邮件内容也可能被一并释放。
  </v-alert>

  <v-card flat class="panel recycle-panel">
    <div class="recycle-toolbar">
      <div class="recycle-filters">
        <v-text-field
          v-model="search"
          label="搜索回收站"
          placeholder="账号、文件夹或 ID"
          prepend-inner-icon="mdi-magnify"
          clearable
          hide-details
          @keyup.enter="applyFilters"
        />
        <v-select v-model="resourceType" :items="resourceTypes" label="资源类型" hide-details />
        <v-select v-model="status" :items="statusTypes" label="清理状态" hide-details />
      </div>
      <div class="recycle-filter-meta">
        <span>{{ activeFilterCount ? `已应用 ${activeFilterCount} 项筛选 · 共 ${totalCount} 个对象` : `共 ${totalCount} 个对象` }}</span>
        <v-btn v-if="can('task.manage')" color="error" variant="tonal" size="small" :disabled="!selectedItems.length || loading || actionLoading" @click="openBulkPurge">永久清理已选 {{ selectedItems.length }} 项</v-btn>
        <v-btn variant="text" size="small" :disabled="!activeFilterCount && !filtersChanged" @click="resetFilters">重置筛选</v-btn>
        <v-btn color="primary" size="small" :disabled="!filtersChanged" @click="applyFilters">应用筛选</v-btn>
      </div>
    </div>

    <v-table class="recycle-table">
      <thead>
        <tr><th v-if="can('task.manage')"><v-checkbox-btn :model-value="allPageSelected" :indeterminate="selectedItems.length > 0 && !allPageSelected" :disabled="!items.length || loading" aria-label="选择当前页全部对象" @click="toggleAllPage" /></th><th>对象</th><th>类型</th><th>状态</th><th>预计占用</th><th>删除时间</th><th>计划清理</th><th class="text-end">操作</th></tr>
      </thead>
      <tbody>
        <tr v-if="loading"><td :colspan="can('task.manage') ? 8 : 7"><v-progress-linear indeterminate color="primary" /></td></tr>
        <tr v-else-if="!items.length && !error">
          <td :colspan="can('task.manage') ? 8 : 7" class="recycle-empty">
            <v-icon size="34">mdi-delete-clock-outline</v-icon>
            <b>{{ activeFilterCount ? '没有符合筛选条件的对象' : '回收站为空' }}</b>
            <span>{{ activeFilterCount ? '调整搜索或筛选条件后重试' : '被软删除的域、账号、邮箱和邮件会显示在这里' }}</span>
          </td>
        </tr>
        <tr v-for="item in items" v-else :key="item.queueId">
          <td v-if="can('task.manage')"><v-checkbox-btn v-model="selectedIds" :value="item.queueId" :aria-label="`选择 ${displayName(item)}`" :disabled="loading || actionLoading" /></td>
          <td class="object-cell">
            <b :title="displayName(item)">{{ displayName(item) }}</b>
            <small>{{ item.resourceType === 'MailboxMessage' ? `${item.displayName} · ` : '' }}队列 {{ shortId(item.queueId) }}<template v-if="item.resourceId"> · 资源 {{ shortId(item.resourceId) }}</template></small>
          </td>
          <td><span class="resource-type"><v-icon size="17">{{ typeView(item.resourceType).icon }}</v-icon>{{ typeView(item.resourceType).label }}</span></td>
          <td><StatusPill v-bind="statusView(item.status)" /></td>
          <td class="bytes-cell">{{ formatBytes(item.estimatedBytes) }}</td>
          <td>{{ formatDate(item.deletedAt) }}</td>
          <td>
            <span class="purge-date">{{ formatDate(item.purgeAfter) }}</span>
            <small :class="{ urgent: purgeWindow(item.purgeAfter).urgent }">{{ purgeWindow(item.purgeAfter).label }}</small>
          </td>
          <td class="action-cell">
            <template v-if="can('task.manage')">
              <v-btn
                size="small"
                variant="text"
                prepend-icon="mdi-restore"
                :disabled="loading || !isRestorable(item)"
                :title="isRestorable(item) ? `恢复 ${displayName(item)}` : restoreUnavailableReason(item)"
                :aria-label="isRestorable(item) ? `恢复 ${displayName(item)}` : `${displayName(item)} 不支持恢复`"
                @click="openAction(item, 'restore')"
              >恢复</v-btn>
              <v-btn
                size="small"
                color="error"
                variant="text"
                prepend-icon="mdi-delete-forever-outline"
                :disabled="loading"
                :aria-label="`永久清理 ${displayName(item)}`"
                @click="openAction(item, 'purge')"
              >永久清理</v-btn>
            </template>
            <span v-else class="permission-note">仅查看</span>
          </td>
        </tr>
      </tbody>
    </v-table>
    <div v-if="!error && totalCount > pageSize" class="recycle-pagination">
      <span>第 {{ page }} / {{ totalPages }} 页</span>
      <v-btn size="small" variant="outlined" :disabled="loading || page <= 1" @click="changePage(page - 1)">上一页</v-btn>
      <v-btn size="small" variant="outlined" :disabled="loading || page >= totalPages" @click="changePage(page + 1)">下一页</v-btn>
    </div>
  </v-card>

  <v-dialog :model-value="Boolean(target) || bulkPurgeOpen" max-width="600" :persistent="actionLoading" @update:model-value="value => { if (!value) closeDialog() }">
    <v-card v-if="target || bulkPurgeOpen">
      <v-card-title class="dialog-title">
        <span class="dialog-icon" :class="bulkPurgeOpen ? 'purge' : action">
          <v-icon>{{ !bulkPurgeOpen && action === 'restore' ? 'mdi-restore' : 'mdi-delete-forever-outline' }}</v-icon>
        </span>
        <span>{{ bulkPurgeOpen ? `永久清理已选 ${selectedItems.length} 项` : action === 'restore' ? '恢复回收站对象' : '永久清理对象' }}</span>
      </v-card-title>
      <v-card-text>
        <v-alert v-if="dialogError" type="error" variant="tonal" density="compact" class="mb-4">{{ dialogError }}</v-alert>

        <div v-if="target" class="dialog-object">
          <span>{{ typeView(target.resourceType).label }}</span>
          <strong>{{ displayName(target) }}</strong>
          <small>预计占用 {{ formatBytes(target.estimatedBytes) }} · 计划于 {{ formatDate(target.purgeAfter) }} 清理</small>
        </div>

        <template v-if="!bulkPurgeOpen && action === 'restore' && target">
          <p class="dialog-copy">{{ target.resourceType === 'MailboxMessage' && target.displayName.includes(' / Trash / ')
            ? 'Trash 中的邮件将恢复到所属账号的收件箱。'
            : '恢复后，对象会重新出现在原有位置。关联对象的恢复范围由资源类型决定。' }}</p>
          <v-alert type="info" variant="tonal" density="compact">该操作会写入审计日志。</v-alert>
        </template>
        <template v-else>
          <v-alert type="error" variant="tonal" density="compact" class="mb-4">
            此操作无法撤销，并会跳过剩余保留期。关联的数据库记录和邮件内容可能被永久释放。
          </v-alert>
          <label class="confirmation-label" for="purge-confirmation">{{ bulkPurgeOpen ? '输入“永久清理”以确认' : '输入对象名称以确认' }}</label>
          <code class="confirmation-value">{{ bulkPurgeOpen ? '永久清理' : target ? displayName(target) : '' }}</code>
          <v-text-field
            id="purge-confirmation"
            v-model="confirmation"
            :label="bulkPurgeOpen ? '永久清理' : '对象名称'"
            autocomplete="off"
            :disabled="actionLoading"
            autofocus
            class="mt-3"
            @keyup.enter="execute"
          />
        </template>
      </v-card-text>
      <v-card-actions class="dialog-actions">
        <v-btn :disabled="actionLoading" @click="closeDialog">取消</v-btn>
        <v-btn
          :color="!bulkPurgeOpen && action === 'restore' ? 'primary' : 'error'"
          :prepend-icon="!bulkPurgeOpen && action === 'restore' ? 'mdi-restore' : 'mdi-delete-forever-outline'"
          :disabled="!canExecute"
          :loading="actionLoading"
          @click="execute"
        >{{ !bulkPurgeOpen && action === 'restore' ? '确认恢复' : '确认永久清理' }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.recycle-summary { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); margin-bottom: 16px; overflow: hidden; background: #fff; border: 1px solid var(--border); border-radius: 8px; }
.recycle-summary > div { display: grid; grid-template-columns: 1fr auto; gap: 2px 14px; min-width: 0; padding: 16px 20px; border-right: 1px solid var(--border); }
.recycle-summary > div:last-child { border-right: 0; }
.recycle-summary span { align-self: center; color: var(--text-secondary); font-size: 12px; font-weight: 650; }
.recycle-summary strong { grid-row: 1 / 3; grid-column: 2; align-self: center; color: var(--text); font-size: 23px; line-height: 1; }
.recycle-summary strong.danger { color: #bd3030; }
.recycle-summary strong.warning { color: #ad6800; }
.recycle-summary small { min-width: 0; overflow: hidden; color: var(--text-muted); font-size: 11px; text-overflow: ellipsis; white-space: nowrap; }
.recycle-warning { margin-bottom: 16px; }
.recycle-panel { overflow: hidden; }
.recycle-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 20px; padding: 16px 18px; border-bottom: 1px solid var(--border); }
.recycle-filters { display: grid; grid-template-columns: minmax(280px, 1.6fr) minmax(160px, .7fr) minmax(150px, .65fr); gap: 10px; width: min(800px, 75%); }
.recycle-filter-meta { display: flex; align-items: center; gap: 8px; color: var(--text-muted); font-size: 12px; white-space: nowrap; }
.recycle-table :deep(th) { white-space: nowrap; }
.recycle-pagination { display: flex; align-items: center; justify-content: flex-end; gap: 10px; padding: 14px 20px; color: var(--text-muted); font-size: 12px; }
.recycle-table :deep(td) { color: var(--text-secondary); font-size: 12px; }
.object-cell { min-width: 250px; }
.object-cell b, .object-cell small, .purge-date, .purge-date + small { display: block; }
.object-cell b { max-width: 360px; overflow: hidden; color: var(--text); font-size: 13px; text-overflow: ellipsis; white-space: nowrap; }
.object-cell small { margin-top: 4px; color: var(--text-muted); font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 10px; }
.resource-type { display: inline-flex; align-items: center; gap: 7px; white-space: nowrap; }
.resource-type :deep(.v-icon) { color: #55769d; }
.bytes-cell { color: var(--text) !important; font-variant-numeric: tabular-nums; font-weight: 650; white-space: nowrap; }
.purge-date { color: var(--text-secondary); white-space: nowrap; }
.purge-date + small { margin-top: 4px; color: var(--text-muted); }
.purge-date + small.urgent { color: #b56d00; font-weight: 700; }
.action-cell { min-width: 218px; text-align: right; white-space: nowrap; }
.permission-note { color: var(--text-muted); }
.recycle-empty { height: 210px; color: var(--text-muted) !important; text-align: center; }
.recycle-empty :deep(.v-icon), .recycle-empty b, .recycle-empty span { display: block; margin: 5px auto; }
.recycle-empty b { color: var(--text-secondary); font-size: 14px; }
.dialog-title { display: flex; align-items: center; gap: 12px; padding-top: 20px; font-size: 17px; }
.dialog-icon { display: grid; width: 38px; height: 38px; place-items: center; color: #1769e0; background: #edf5ff; border-radius: 8px; }
.dialog-icon.purge { color: #bd3030; background: #fff0f0; }
.dialog-object { display: flex; flex-direction: column; gap: 4px; margin-bottom: 18px; padding: 14px 16px; background: #f7f9fc; border: 1px solid var(--border); border-radius: 8px; }
.dialog-object span { color: var(--text-muted); font-size: 11px; }
.dialog-object strong { overflow-wrap: anywhere; color: var(--text); font-size: 14px; }
.dialog-object small { color: var(--text-muted); font-size: 11px; }
.dialog-copy { margin: 0 0 16px; color: var(--text-secondary); font-size: 13px; line-height: 1.7; }
.confirmation-label { display: block; color: var(--text-secondary); font-size: 12px; font-weight: 650; }
.confirmation-value { display: block; margin-top: 6px; overflow-wrap: anywhere; color: #9c2929; font-size: 12px; }
.dialog-actions { padding: 12px 20px 18px; }
@media (max-width: 1280px) {
  .recycle-summary { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .recycle-summary > div:nth-child(2) { border-right: 0; }
  .recycle-summary > div:nth-child(-n + 2) { border-bottom: 1px solid var(--border); }
  .recycle-toolbar { align-items: stretch; flex-direction: column; }
  .recycle-filters { width: 100%; }
  .recycle-filter-meta { justify-content: space-between; }
}
@media (max-width: 720px) {
  .recycle-summary { grid-template-columns: 1fr; }
  .recycle-summary > div { border-right: 0; border-bottom: 1px solid var(--border); }
  .recycle-summary > div:nth-child(2) { border-right: 0; }
  .recycle-summary > div:nth-child(-n + 3) { border-bottom: 1px solid var(--border); }
  .recycle-summary > div:last-child { border-bottom: 0; }
  .recycle-filters { grid-template-columns: 1fr; }
  .recycle-filter-meta { align-items: flex-start; flex-direction: column; }
}
</style>
