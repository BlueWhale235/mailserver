<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref, watch } from 'vue'
import { api } from '../../lib/api'
import StatusPill from '../../components/StatusPill.vue'
import { usePermission } from '../../composables/usePermission'
import DeliveryJobDetailDialog from './DeliveryJobDetailDialog.vue'
import { deliveryStatusPresentation, formatDate, shortId } from './deliveryPresentation'
import type { DeliveryBatchResult, DeliveryJob, DeliveryStatus, DeliveryWorkspace, PagedResult } from './types'

const props = defineProps<{
  mode: DeliveryWorkspace
  refreshKey: number
  domainId?: string
}>()

const emit = defineEmits<{ changed: [] }>()
const { can } = usePermission()

const rows = ref<DeliveryJob[]>([])
const selected = ref<string[]>([])
const search = ref<string | null>('')
const searchText = computed(() => search.value?.trim() ?? '')
const status = ref<DeliveryStatus | null>(null)
const page = ref(1)
const pageSize = 20
const totalCount = ref(0)
const loading = ref(false)
const actionBusy = ref(false)
const actionId = ref('')
const error = ref('')
const notice = ref('')
const detailTarget = ref<DeliveryJob | null>(null)
const detailOpen = ref(false)
const deleteTargets = ref<DeliveryJob[]>([])
const deleteConfirmation = ref('')
let searchTimer: ReturnType<typeof globalThis.setTimeout> | undefined
let loadVersion = 0

const canManage = computed(() => can('queue.manage'))
const scope = computed(() => props.mode === 'history' ? 'History' : props.mode === 'bounces' ? 'Failed' : 'Active')
const showStatusFilter = computed(() => props.mode === 'queue' || Boolean(props.domainId))
const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))
const selectable = computed(() => canManage.value && props.mode !== 'history')
const selectableRows = computed(() => rows.value.filter(row => row.status !== 'Processing'))
const allSelected = computed(() => selectableRows.value.length > 0 && selectableRows.value.every(row => selected.value.includes(row.id)))
const retryableSelected = computed(() => rows.value.filter(row => selected.value.includes(row.id) && (row.status === 'Failed' || row.status === 'Deferred')))
const title = computed(() => props.mode === 'history' ? '投递历史' : props.mode === 'bounces' ? '退信与永久失败' : '实时投递队列')
const description = computed(() => props.mode === 'history'
  ? '保留已完成投递的远端响应和协议证据。'
  : props.mode === 'bounces'
    ? '永久失败任务会进入退信处理；空信封发件人不会继续生成退信。'
    : '按更新时间展示等待、处理中、延迟与失败任务。')
const emptyText = computed(() => searchText.value || status.value
  ? '没有符合筛选条件的投递任务'
  : props.mode === 'history'
    ? '尚无已完成投递记录'
    : props.mode === 'bounces'
      ? '当前没有永久失败任务'
      : '当前投递队列为空')

const statusItems = [
  { title: '全部队列状态', value: null },
  { title: '等待投递', value: 'Pending' },
  { title: '投递中', value: 'Processing' },
  { title: '延迟重试', value: 'Deferred' },
  { title: '永久失败', value: 'Failed' },
]

function queuePath(suffix = '') {
  const base = props.domainId ? '/domains/' + encodeURIComponent(props.domainId) + '/queue' : '/queues'
  return base + suffix
}

function queryString() {
  const query = new globalThis.URLSearchParams({
    scope: scope.value,
    page: String(page.value),
    pageSize: String(pageSize),
  })
  if (searchText.value) query.set('search', searchText.value)
  if (showStatusFilter.value && status.value) query.set('status', status.value)
  return query.toString()
}

async function load() {
  const version = ++loadVersion
  loading.value = true
  error.value = ''
  try {
    const result = await api<PagedResult<DeliveryJob>>(`${queuePath()}?${queryString()}`)
    if (version !== loadVersion) return
    rows.value = result.items
    totalCount.value = result.totalCount
    selected.value = selected.value.filter(id => result.items.some(item => item.id === id && item.status !== 'Processing'))
    if (page.value > pageCount.value) page.value = pageCount.value
  } catch (cause) {
    if (version !== loadVersion) return
    rows.value = []
    totalCount.value = 0
    error.value = cause instanceof Error ? cause.message : '投递任务加载失败'
  } finally { if (version === loadVersion) loading.value = false }
}

function toggleAll(value: boolean | null) {
  selected.value = value ? selectableRows.value.map(row => row.id) : []
}

function toggleOne(id: string, value: boolean | null) {
  if (!selectableRows.value.some(row => row.id === id)) return
  selected.value = value ? Array.from(new Set([...selected.value, id])) : selected.value.filter(item => item !== id)
}

function openDetail(job: DeliveryJob) {
  detailTarget.value = job
  detailOpen.value = true
}

async function retry(job: DeliveryJob) {
  if (!canManage.value || actionBusy.value) return
  actionBusy.value = true
  actionId.value = job.id
  error.value = ''
  notice.value = ''
  try {
    await api(queuePath('/' + job.id + '/retry'), {
      method: 'POST', body: JSON.stringify({ expectedAttempts: job.attempts }),
    })
    notice.value = `已安排立即重试 ${job.recipient}`
    await load()
    emit('changed')
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '重试请求失败' }
  finally { actionId.value = ''; actionBusy.value = false }
}

async function retrySelected() {
  if (!retryableSelected.value.length || actionBusy.value || !canManage.value) return
  actionBusy.value = true
  error.value = ''
  notice.value = ''
  const jobs = [...retryableSelected.value]
  try {
    const result = await api<DeliveryBatchResult>(queuePath('/batch/retry'), {
      method: 'POST', body: JSON.stringify({
        ids: jobs.map(job => job.id),
        expectedAttempts: Object.fromEntries(jobs.map(job => [job.id, job.attempts])),
      }),
    })
    const failed = result.items.filter(item => item.status !== 'Succeeded')
    notice.value = result.succeeded ? `已安排 ${result.succeeded} 个任务立即重试` : ''
    selected.value = failed.map(item => item.id)
    await load()
    if (failed.length) error.value = failed.map(item => `${shortId(item.id)}：${item.detail || item.status}`).join('；')
    emit('changed')
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '批量重试失败' }
  finally { actionBusy.value = false }
}

function requestDelete(jobs: DeliveryJob[]) {
  if (!canManage.value || actionBusy.value || !jobs.length || jobs.some(job => job.status === 'Processing')) return
  deleteTargets.value = jobs
  deleteConfirmation.value = ''
}

async function confirmDelete() {
  if (!deleteTargets.value.length || deleteConfirmation.value !== '永久删除' || actionBusy.value || !canManage.value) return
  actionBusy.value = true
  error.value = ''
  notice.value = ''
  const jobs = [...deleteTargets.value]
  try {
    const result = await api<DeliveryBatchResult>(queuePath('/batch/delete'), {
      method: 'POST', body: JSON.stringify({ ids: jobs.map(job => job.id) }),
    })
    const failed = result.items.filter(item => item.status !== 'Succeeded')
    notice.value = result.succeeded ? `已永久删除 ${result.succeeded} 个投递任务` : ''
    deleteTargets.value = []
    selected.value = failed.map(item => item.id)
    await load()
    if (failed.length) error.value = failed.map(item => `${shortId(item.id)}：${item.detail || item.status}`).join('；')
    emit('changed')
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '删除投递任务失败' }
  finally { actionBusy.value = false }
}

watch(search, () => {
  ++loadVersion
  globalThis.clearTimeout(searchTimer)
  searchTimer = globalThis.setTimeout(resetPageAndLoad, 350)
})
function resetPageAndLoad() {
  selected.value = []
  if (page.value !== 1) page.value = 1
  else void load()
}
watch(status, resetPageAndLoad)
watch(page, () => { selected.value = []; void load() })
watch(() => props.mode, () => {
  search.value = ''
  status.value = null
  notice.value = ''
  detailOpen.value = false
  resetPageAndLoad()
})
watch(() => props.refreshKey, () => { void load() })
onMounted(load)
onBeforeUnmount(() => { ++loadVersion; globalThis.clearTimeout(searchTimer) })
</script>

<template>
  <section class="panel table-panel delivery-workspace">
    <div class="panel-head delivery-workspace-head">
      <div><h2>{{ title }}</h2><p>{{ description }}</p></div>
      <div class="delivery-head-actions">
        <v-btn v-if="selectable" variant="outlined" prepend-icon="mdi-refresh" :disabled="!retryableSelected.length || actionBusy" :loading="actionBusy" @click="retrySelected">重试所选</v-btn>
        <v-btn v-if="selectable" variant="outlined" color="error" prepend-icon="mdi-delete-outline" :disabled="!selected.length || actionBusy" @click="requestDelete(rows.filter(row => selected.includes(row.id)))">删除所选</v-btn>
        <v-btn icon="mdi-refresh" variant="text" aria-label="刷新投递列表" :loading="loading" :disabled="actionBusy" @click="load" />
      </div>
    </div>

    <v-alert v-if="error" type="error" variant="tonal" class="mx-4 mb-3" closable @click:close="error = ''">{{ error }}</v-alert>
    <v-alert v-if="notice" type="success" variant="tonal" class="mx-4 mb-3" closable @click:close="notice = ''">{{ notice }}</v-alert>

    <div class="table-toolbar delivery-filterbar">
      <v-text-field v-model="search" prepend-inner-icon="mdi-magnify" placeholder="搜索发件人、收件人或错误" hide-details density="compact" clearable />
      <v-select v-if="showStatusFilter" v-model="status" :items="statusItems" hide-details density="compact" />
      <v-spacer />
      <span class="delivery-result-count">共 {{ totalCount }} 个任务<span v-if="selected.length"> · 已选择 {{ selected.length }} 个</span></span>
    </div>

    <div class="delivery-table-scroll">
      <v-table class="delivery-table">
        <thead><tr>
          <th v-if="selectable" class="select-column"><v-checkbox-btn :model-value="allSelected" :disabled="!selectableRows.length" aria-label="选择当前页可操作任务" @update:model-value="toggleAll" /></th>
          <th>邮件流向</th><th>状态</th><th>尝试</th><th>{{ mode === 'history' ? '远端结果' : '最近错误' }}</th><th>{{ mode === 'history' ? '完成时间' : '下次动作 / 更新时间' }}</th><th class="actions-column">操作</th>
        </tr></thead>
        <tbody>
          <tr v-if="loading"><td :colspan="selectable ? 7 : 6"><v-progress-linear indeterminate color="primary" /></td></tr>
          <tr v-else-if="!rows.length"><td :colspan="selectable ? 7 : 6" class="delivery-empty-cell"><v-icon size="28">mdi-inbox-arrow-down-outline</v-icon><b>{{ emptyText }}</b><span>调整筛选条件或稍后刷新。</span></td></tr>
          <tr v-for="job in rows" v-else :key="job.id">
            <td v-if="selectable" class="select-column"><v-checkbox-btn :model-value="selected.includes(job.id)" :disabled="job.status === 'Processing'" :aria-label="job.status === 'Processing' ? '投递中任务暂不可删除' : `选择投递任务 ${shortId(job.id)}`" @update:model-value="value => toggleOne(job.id, value)" /></td>
            <td class="route-cell"><b>{{ job.sender }}</b><span><v-icon size="13">mdi-arrow-right</v-icon>{{ job.recipient }}</span><code>{{ shortId(job.id) }}</code></td>
            <td><StatusPill v-bind="deliveryStatusPresentation(job.status)" /></td>
            <td><b>{{ job.attempts }}</b><small>{{ job.attempts ? '次已记录' : '尚未开始' }}</small></td>
            <td class="response-cell"><span :class="{ 'text-error': job.status === 'Failed' }">{{ job.lastError || (job.status === 'Delivered' ? '投递完成 · 响应详见投递尝试' : '—') }}</span></td>
            <td>{{ job.status === 'Pending' || job.status === 'Deferred' ? formatDate(job.nextAttemptAt) : formatDate(job.updatedAt) }}<small>{{ job.status === 'Pending' ? '计划投递' : job.status === 'Deferred' ? '计划重试' : '最近更新' }}</small></td>
            <td class="actions-column">
              <v-btn size="small" variant="text" prepend-icon="mdi-timeline-text-outline" @click="openDetail(job)">详情</v-btn>
              <v-tooltip v-if="canManage && (job.status === 'Failed' || job.status === 'Deferred')" text="立即重新进入投递队列" location="top">
                <template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-refresh" size="small" variant="text" aria-label="立即重试" :loading="actionId === job.id" :disabled="actionBusy" @click="retry(job)" /></template>
              </v-tooltip>
              <v-tooltip v-if="selectable && job.status !== 'Processing'" text="永久删除任务" location="top">
                <template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-delete-outline" size="small" color="error" variant="text" aria-label="删除投递任务" :disabled="actionBusy" @click="requestDelete([job])" /></template>
              </v-tooltip>
            </td>
          </tr>
        </tbody>
      </v-table>
    </div>
    <div class="table-footer"><span>第 {{ page }} / {{ pageCount }} 页 · 每页 {{ pageSize }} 条</span><v-pagination v-model="page" :length="pageCount" density="compact" /></div>
  </section>

  <DeliveryJobDetailDialog v-model="detailOpen" :job="detailTarget" :domain-id="domainId" />

  <v-dialog :model-value="deleteTargets.length > 0" max-width="540" persistent @update:model-value="value => { if (!value && !actionBusy) deleteTargets = [] }">
    <v-card>
      <v-card-title class="d-flex align-center ga-2"><v-icon color="error">mdi-delete-alert-outline</v-icon>永久删除投递任务</v-card-title>
      <v-card-text>
        <v-alert type="warning" variant="tonal" class="mb-4">将永久删除 {{ deleteTargets.length }} 个任务。若原始邮件不再被邮箱、已发副本或其他任务引用，邮件内容也会进入清理流程。</v-alert>
        <p class="mb-3">请输入「永久删除」确认。</p>
        <v-text-field v-model="deleteConfirmation" label="确认文字" autocomplete="off" :readonly="actionBusy" autofocus />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn :disabled="actionBusy" @click="deleteTargets = []">取消</v-btn><v-btn color="error" :loading="actionBusy" :disabled="deleteConfirmation !== '永久删除'" @click="confirmDelete">确认删除</v-btn></v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.delivery-workspace { min-width: 0; }
.delivery-workspace-head { align-items: center; padding-bottom: 18px; }
.delivery-head-actions { display: flex; align-items: center; gap: 8px; }
.delivery-filterbar { border-top: 1px solid var(--border); }
.delivery-result-count { color: var(--text-muted); font-size: 12px; white-space: nowrap; }
.delivery-table-scroll { overflow-x: auto; }
.delivery-table { min-width: 1120px; }
.delivery-table :deep(th:first-child), .delivery-table :deep(td:first-child) { padding-left: 18px !important; }
.select-column { width: 52px; }
.actions-column { width: 170px; white-space: nowrap; }
.route-cell { min-width: 240px; }
.route-cell b, .route-cell span, .route-cell code { display: block; }
.route-cell span { margin-top: 4px; color: var(--text-secondary); }
.route-cell span .v-icon { margin-right: 5px; }
.route-cell code { width: max-content; margin-top: 5px; padding: 2px 6px; border-radius: 4px; background: var(--bg-app); color: var(--text-muted); font-size: 10px; }
.response-cell { width: 290px; max-width: 290px; }
.response-cell span { display: -webkit-box; overflow: hidden; overflow-wrap: anywhere; -webkit-box-orient: vertical; -webkit-line-clamp: 2; line-height: 1.45; }
.delivery-empty-cell { height: 220px !important; text-align: center; color: var(--text-muted) !important; }
.delivery-empty-cell .v-icon, .delivery-empty-cell b, .delivery-empty-cell span { display: block; margin: 5px auto; }
.delivery-empty-cell b { color: var(--text); }
@media (max-width: 900px) {
  .delivery-workspace-head { align-items: flex-start; flex-direction: column; }
  .delivery-filterbar { flex-wrap: wrap; }
  .delivery-result-count { width: 100%; }
}
</style>
