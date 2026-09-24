<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref, watch } from 'vue'
import { api } from '../../lib/api'
import StatusPill from '../../components/StatusPill.vue'
import { usePermission } from '../../composables/usePermission'
import { formatDate, shortId, tlsReportStatusPresentation } from './deliveryPresentation'
import type { PagedResult, TlsReportDelivery, TlsReportDeliveryDetail, TlsReportStatus } from './types'

const props = defineProps<{ refreshKey: number }>()
const { can } = usePermission()
const rows = ref<TlsReportDelivery[]>([])
const search = ref<string | null>('')
const status = ref<TlsReportStatus | null>(null)
const page = ref(1)
const pageSize = 20
const totalCount = ref(0)
const loading = ref(false)
const actionId = ref('')
const retryTarget = ref<TlsReportDelivery | null>(null)
const retryKey = ref('')
const error = ref('')
const notice = ref('')
const detailOpen = ref(false)
const detailLoading = ref(false)
const detail = ref<TlsReportDeliveryDetail | null>(null)
let searchTimer: ReturnType<typeof globalThis.setTimeout> | undefined
let loadVersion = 0
let detailVersion = 0

const canManage = computed(() => can('queue.manage'))
const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))
const statusItems = [
  { title: '全部状态', value: null },
  { title: '待发送', value: 'Pending' },
  { title: '等待重试', value: 'Retrying' },
  { title: '重试耗尽', value: 'Failed' },
  { title: '已送达', value: 'Completed' },
]

function queryString() {
  const query = new globalThis.URLSearchParams({ page: String(page.value), pageSize: String(pageSize) })
  if (search.value?.trim()) query.set('search', search.value.trim())
  if (status.value) query.set('status', status.value)
  return query.toString()
}

async function load() {
  const version = ++loadVersion
  loading.value = true
  error.value = ''
  try {
    const result = await api<PagedResult<TlsReportDelivery>>(`/delivery/tls-reports?${queryString()}`)
    if (version !== loadVersion) return
    rows.value = result.items
    totalCount.value = result.totalCount
    if (page.value > pageCount.value) page.value = pageCount.value
  } catch (cause) {
    if (version !== loadVersion) return
    rows.value = []
    totalCount.value = 0
    error.value = cause instanceof Error ? cause.message : 'TLS 报告加载失败'
  } finally { if (version === loadVersion) loading.value = false }
}

async function openDetail(report: TlsReportDelivery) {
  const version = ++detailVersion
  detailOpen.value = true
  detailLoading.value = true
  detail.value = null
  error.value = ''
  try {
    const result = await api<TlsReportDeliveryDetail>(`/delivery/tls-reports/${report.id}`)
    if (version === detailVersion) detail.value = result
  }
  catch (cause) { if (version === detailVersion) { error.value = cause instanceof Error ? cause.message : 'TLS 报告详情加载失败'; detailOpen.value = false } }
  finally { if (version === detailVersion) detailLoading.value = false }
}

function retry(report: TlsReportDelivery) {
  if (!canManage.value || !['Failed', 'Retrying'].includes(report.status) || actionId.value) return
  error.value = ''
  notice.value = ''
  retryTarget.value = report
  retryKey.value = globalThis.crypto.randomUUID()
}

async function confirmRetry() {
  const report = retryTarget.value
  if (!report || actionId.value) return
  actionId.value = report.id
  error.value = ''
  notice.value = ''
  try {
    const result = await api<{ scheduled: boolean; replayed: boolean }>(`/delivery/tls-reports/${report.id}/retry`, {
      method: 'POST', headers: { 'Idempotency-Key': retryKey.value },
    })
    notice.value = result.scheduled
      ? `已安排重新发送 ${report.policyDomain} 的 TLS 报告`
      : result.replayed ? '这次重试请求已处理，无需重复提交' : '该报告已经安排投递'
    retryTarget.value = null
    await load()
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'TLS 报告重试失败' }
  finally { actionId.value = '' }
}

function downloadJson() {
  if (!detail.value) return
  const url = globalThis.URL.createObjectURL(new globalThis.Blob([detail.value.reportJson], { type: 'application/tlsrpt+json;charset=utf-8' }))
  const anchor = globalThis.document.createElement('a')
  anchor.href = url
  anchor.download = `tls-report-${detail.value.policyDomain}-${detail.value.periodStart.slice(0, 10)}.json`
  anchor.click()
  globalThis.setTimeout(() => globalThis.URL.revokeObjectURL(url), 0)
}

watch(search, () => {
  ++loadVersion
  globalThis.clearTimeout(searchTimer)
  searchTimer = globalThis.setTimeout(resetPageAndLoad, 350)
})
function resetPageAndLoad() {
  if (page.value !== 1) page.value = 1
  else void load()
}
watch(status, resetPageAndLoad)
watch(page, () => { void load() })
watch(detailOpen, value => { if (!value) ++detailVersion })
watch(() => props.refreshKey, () => { void load() })
onMounted(load)
onBeforeUnmount(() => { ++loadVersion; ++detailVersion; globalThis.clearTimeout(searchTimer) })
</script>

<template>
  <section class="panel table-panel tls-report-workspace">
    <div class="panel-head tls-report-head"><div><h2>TLS 聚合报告</h2><p>查看 RFC 8460 报告的生成周期、目标 URI 和投递结果。</p></div><v-btn icon="mdi-refresh" variant="text" aria-label="刷新 TLS 报告" :loading="loading" @click="load" /></div>
    <v-alert v-if="error" type="error" variant="tonal" class="mx-4 mb-3" closable @click:close="error = ''">{{ error }}</v-alert>
    <v-alert v-if="notice" type="success" variant="tonal" class="mx-4 mb-3" closable @click:close="notice = ''">{{ notice }}</v-alert>
    <div class="tls-report-note"><v-icon>mdi-shield-check-outline</v-icon><p><b>报告聚合传输安全结果，不包含邮件正文</b><span>包括 TLS 会话计数、策略和失败原因；报告可能包含管理员联系地址。</span></p></div>
    <div class="table-toolbar">
      <v-text-field v-model="search" prepend-inner-icon="mdi-magnify" placeholder="搜索策略域、报告 URI 或错误" hide-details density="compact" clearable />
      <v-select v-model="status" :items="statusItems" hide-details density="compact" />
      <v-spacer /><span class="text-caption text-medium-emphasis">共 {{ totalCount }} 份报告</span>
    </div>
    <div class="tls-table-scroll">
      <v-table class="tls-table">
        <thead><tr><th>策略域</th><th>报告周期</th><th>目标 URI</th><th>状态</th><th>尝试</th><th>下一动作 / 完成时间</th><th>操作</th></tr></thead>
        <tbody>
          <tr v-if="loading"><td colspan="7"><v-progress-linear indeterminate color="primary" /></td></tr>
          <tr v-else-if="!rows.length"><td colspan="7" class="text-center py-10 text-medium-emphasis">没有符合条件的 TLS 报告</td></tr>
          <tr v-for="report in rows" v-else :key="report.id">
            <td><b>{{ report.policyDomain }}</b><small><code>{{ shortId(report.id) }}</code></small></td>
            <td>{{ new Date(report.periodStart).toLocaleDateString('zh-CN') }}<small>至 {{ new Date(report.periodEnd).toLocaleDateString('zh-CN') }}</small></td>
            <td class="report-uri">{{ report.reportUri }}</td>
            <td><StatusPill v-bind="tlsReportStatusPresentation(report.status)" /><small v-if="report.lastError" class="text-error">{{ report.lastError }}</small></td>
            <td>{{ report.attempts }} / 5</td>
            <td>{{ report.status === 'Failed' ? '等待管理员处理' : formatDate(report.completedAt || report.nextAttemptAt) }}<small>{{ report.completedAt ? '已完成' : report.status === 'Failed' ? '自动重试已停止' : '下一次尝试' }}</small></td>
            <td class="text-no-wrap"><v-btn size="small" variant="text" prepend-icon="mdi-code-json" @click="openDetail(report)">详情</v-btn><v-btn v-if="canManage && ['Failed', 'Retrying'].includes(report.status)" icon="mdi-refresh" size="small" variant="text" aria-label="重试 TLS 报告" :loading="actionId === report.id" @click="retry(report)" /></td>
          </tr>
        </tbody>
      </v-table>
    </div>
    <div class="table-footer"><span>第 {{ page }} / {{ pageCount }} 页</span><v-pagination v-model="page" :length="pageCount" density="compact" /></div>
  </section>

  <v-dialog :model-value="!!retryTarget" max-width="500" persistent>
    <v-card>
      <v-card-title>确认重试 TLS 报告</v-card-title>
      <v-card-text>将重新安排向 {{ retryTarget?.reportUri }} 发送 {{ retryTarget?.policyDomain }} 的 TLS 聚合报告。</v-card-text>
      <v-alert v-if="error" type="error" variant="tonal" class="mx-4 mb-2">{{ error }}</v-alert>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="!!actionId" @click="retryTarget = null">取消</v-btn><v-btn color="primary" :loading="!!actionId" @click="confirmRetry">确认重试</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog v-model="detailOpen" max-width="980" scrollable>
    <v-card class="tls-detail-dialog">
      <v-card-title class="d-flex align-center"><span>TLS 报告详情</span><v-spacer /><v-btn icon="mdi-close" variant="text" aria-label="关闭 TLS 报告详情" @click="detailOpen = false" /></v-card-title>
      <v-progress-linear v-if="detailLoading" indeterminate color="primary" />
      <v-card-text v-else-if="detail">
        <div class="tls-detail-facts">
          <div><span>策略域</span><b>{{ detail.policyDomain }}</b></div>
          <div><span>投递状态</span><StatusPill v-bind="tlsReportStatusPresentation(detail.status)" /></div>
          <div><span>目标</span><b>{{ detail.reportUri }}</b></div>
          <div><span>周期</span><b>{{ formatDate(detail.periodStart) }} — {{ formatDate(detail.periodEnd) }}</b></div>
        </div>
        <div class="json-head"><div><b>application/tlsrpt+json</b><span>报告正文按原始字段展示。</span></div><v-btn variant="outlined" prepend-icon="mdi-download-outline" @click="downloadJson">下载 JSON</v-btn></div>
        <pre class="tls-json">{{ detail.reportJson }}</pre>
      </v-card-text>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.tls-report-head { align-items: center; }
.tls-report-note { display: flex; align-items: flex-start; gap: 11px; margin: 0 18px 16px; padding: 13px 15px; border: 1px solid #d8e4f5; border-radius: 8px; background: #f5f8fd; color: #315f9d; }
.tls-report-note p { margin: 0; }
.tls-report-note b, .tls-report-note span { display: block; }
.tls-report-note b { font-size: 12px; }
.tls-report-note span { margin-top: 3px; color: #6d80a0; font-size: 11px; }
.tls-table-scroll { overflow-x: auto; }
.tls-table { min-width: 1080px; }
.report-uri { max-width: 260px; overflow-wrap: anywhere; }
.tls-detail-dialog { border-radius: 12px !important; }
.tls-detail-facts { display: grid; grid-template-columns: 1fr 1fr; border: 1px solid var(--border); border-radius: 8px; }
.tls-detail-facts div { padding: 13px 15px; border-bottom: 1px solid var(--border); }
.tls-detail-facts div:nth-child(odd) { border-right: 1px solid var(--border); }
.tls-detail-facts div:nth-last-child(-n+2) { border-bottom: 0; }
.tls-detail-facts span, .tls-detail-facts b { display: block; }
.tls-detail-facts span { color: var(--text-muted); font-size: 11px; margin-bottom: 5px; }
.tls-detail-facts b { font-size: 12px; overflow-wrap: anywhere; }
.json-head { display: flex; align-items: center; justify-content: space-between; gap: 20px; margin: 20px 0 10px; }
.json-head b, .json-head span { display: block; }
.json-head span { color: var(--text-muted); font-size: 11px; margin-top: 3px; }
.tls-json { margin: 0; min-height: 360px; max-height: 58vh; overflow: auto; padding: 17px; border-radius: 8px; background: #111827; color: #d8e2f2; font: 12px/1.65 ui-monospace, SFMono-Regular, Consolas, monospace; white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
