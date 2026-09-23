<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import PageHeader from '../components/PageHeader.vue'
import StatusPill from '../components/StatusPill.vue'
import { api, jsonBody } from '../lib/api'
import { useAuthStore } from '../stores/auth'
import { downloadQuarantineRaw, quarantineStatuses, type QuarantineDetail, type QuarantinePage, type QuarantineRecord, type QuarantineStatus } from '../lib/quarantine'

const auth = useAuthStore()
const domains = ref<Array<{ id: string; name: string }>>([])
const domainId = ref('')
const domainLoading = ref(false)
const search = ref<string | null>('')
const status = ref<QuarantineStatus | null>('PendingReview')
const page = ref(1)
const rows = ref<QuarantineRecord[]>([])
const total = ref(0)
const loading = ref(false)
const error = ref('')
const notice = ref('')
const detailOpen = ref(false)
const detail = ref<QuarantineDetail | null>(null)
const detailLoading = ref(false)
const detailError = ref('')
const downloadLoading = ref(false)
const target = ref<QuarantineRecord | null>(null)
const action = ref<'release' | 'delete' | null>(null)
const note = ref('')
const confirmation = ref('')
const actionError = ref('')
const saving = ref(false)
let version = 0
let detailVersion = 0
let timer: ReturnType<typeof globalThis.setTimeout> | undefined
const pages = computed(() => Math.max(1, Math.ceil(total.value / 20)))
const canManage = computed(() => !!domainId.value && auth.can('domain.quarantine.manage', domainId.value))
const actionLabel = computed(() => action.value === 'release' ? '放行' : '删除')
const date = (value?: string) => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
const label = (value: QuarantineStatus) => quarantineStatuses.find(item => item.value === value)?.title ?? value

async function loadDomains() {
  domainLoading.value = true; error.value = ''
  try {
    domains.value = await api('/quarantine/domains')
    if (!domainId.value) domainId.value = domains.value[0]?.id ?? ''
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '域列表加载失败' }
  finally { domainLoading.value = false }
}
async function load() {
  const current = ++version
  if (!domainId.value) { rows.value = []; total.value = 0; loading.value = false; return }
  loading.value = true; error.value = ''
  const query = new globalThis.URLSearchParams({ page: String(page.value), pageSize: '20' })
  if (search.value?.trim()) query.set('search', search.value.trim())
  if (status.value) query.set('status', status.value)
  try {
    const result = await api<QuarantinePage>(`/domains/${domainId.value}/quarantine?${query}`)
    if (current !== version) return
    rows.value = result.items; total.value = result.total
    if (page.value > pages.value) page.value = pages.value
  } catch (cause) { if (current === version) { rows.value = []; total.value = 0; error.value = cause instanceof Error ? cause.message : '隔离记录加载失败' } }
  finally { if (current === version) loading.value = false }
}
function resetAndLoad() { if (page.value !== 1) page.value = 1; else void load() }
async function review(record: QuarantineRecord) {
  const current = ++detailVersion
  target.value = record; detail.value = null; detailOpen.value = true; detailLoading.value = true; detailError.value = ''
  try {
    const result = await api<QuarantineDetail>(`/domains/${record.domainId}/quarantine/${record.id}/raw`)
    if (current === detailVersion) { detail.value = result; target.value = result.record }
  } catch (cause) { if (current === detailVersion) detailError.value = cause instanceof Error ? cause.message : '邮件原文读取失败' }
  finally { if (current === detailVersion) detailLoading.value = false }
}
async function downloadRaw() {
  if (!target.value || downloadLoading.value) return
  downloadLoading.value = true; detailError.value = ''
  try {
    const fileName = await downloadQuarantineRaw(target.value.domainId, target.value.id)
    notice.value = `完整邮件原文已下载：${fileName}`
  } catch (cause) { detailError.value = cause instanceof Error ? cause.message : '邮件原文下载失败' }
  finally { downloadLoading.value = false }
}
function requestAction(kind: 'release' | 'delete') {
  if (!target.value || target.value.status !== 'PendingReview' || !canManage.value || saving.value) return
  action.value = kind; note.value = ''; confirmation.value = ''; actionError.value = ''
}
async function execute() {
  if (!action.value || !target.value || !canManage.value || saving.value || confirmation.value !== actionLabel.value) return
  const record = target.value; const kind = action.value
  saving.value = true; actionError.value = ''
  try {
    await api(`/domains/${record.domainId}/quarantine/${record.id}${kind === 'release' ? '/release' : ''}`, {
      method: kind === 'release' ? 'POST' : 'DELETE', body: jsonBody({ note: note.value.trim() || null }),
    })
    notice.value = kind === 'release' ? '邮件已放行至原收件人的收件箱。' : '隔离记录已标记删除，不再允许放行。'
    action.value = null; detailOpen.value = false
    await load()
  } catch (cause) { actionError.value = cause instanceof Error ? cause.message : '处理失败，请刷新记录后重试' }
  finally { saving.value = false }
}
function cancelAction() {
  if (saving.value) return
  if (note.value.trim() && !globalThis.confirm('审核备注尚未提交，确定放弃？')) return
  action.value = null
}
watch(domainId, () => { detailOpen.value = false; rows.value = []; notice.value = ''; resetAndLoad() })
watch(status, resetAndLoad)
watch(page, load)
watch(search, () => { ++version; globalThis.clearTimeout(timer); timer = globalThis.setTimeout(resetAndLoad, 350) })
watch(detailOpen, value => { if (!value) ++detailVersion })
onBeforeRouteLeave(() => !saving.value && (!action.value || !note.value.trim() || globalThis.confirm('审核备注尚未提交，确定离开？')))
onBeforeUnmount(() => { ++version; ++detailVersion; globalThis.clearTimeout(timer) })
onMounted(loadDomains)
</script>

<template>
  <PageHeader title="隔离区" description="按域审核被插件隔离的邮件；放行前请核对原因与原始内容">
    <v-btn to="/security" variant="outlined">返回邮件安全</v-btn>
    <v-btn :loading="loading || domainLoading" :disabled="saving" @click="domainId ? load() : loadDomains()">刷新</v-btn>
  </PageHeader>
  <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>
  <section class="panel">
    <div class="panel-head"><div><h2>隔离邮件</h2><p>仅显示当前账号获授权的域；已处理记录保留审核轨迹。</p></div><span>{{ total }} 条记录</span></div>
    <div class="table-toolbar">
      <v-select v-model="domainId" :items="domains" item-title="name" item-value="id" label="邮件域" :loading="domainLoading" :disabled="saving" hide-details />
      <v-text-field v-model="search" label="搜索地址、原因或插件" clearable hide-details />
      <v-select v-model="status" :items="[{ title: '全部状态', value: null }, ...quarantineStatuses]" label="审核状态" hide-details />
    </div>
    <v-progress-linear v-if="loading || domainLoading" indeterminate />
    <v-table><thead><tr><th>邮件流向</th><th>隔离原因 / 插件</th><th>状态</th><th>隔离时间</th><th>大小</th><th>操作</th></tr></thead><tbody>
      <tr v-for="record in rows" :key="record.id"><td><b>{{ record.sender || '空信封发件人' }}</b><small>{{ record.recipients.join('、') }}</small></td><td>{{ record.reason }}<small>{{ record.pluginName || record.pluginId || '未记录插件' }}</small></td><td><StatusPill :label="label(record.status)" :tone="record.status === 'PendingReview' ? 'warning' : record.status === 'Released' ? 'success' : 'neutral'" /></td><td>{{ date(record.quarantinedAt) }}</td><td>{{ (record.size / 1024).toFixed(1) }} KB</td><td><v-btn variant="text" @click="review(record)">查看与审核</v-btn></td></tr>
      <tr v-if="!rows.length && !loading && !domainLoading"><td colspan="6" class="text-center py-10">{{ error ? '读取失败，请重试' : !domains.length ? '没有可访问的隔离域' : '没有符合条件的隔离邮件' }}</td></tr>
    </tbody></v-table>
    <div class="table-footer"><span>第 {{ page }} / {{ pages }} 页 · 每页 20 条</span><v-pagination v-model="page" :length="pages" density="compact" /></div>
  </section>
  <v-dialog v-model="detailOpen" max-width="1080" scrollable :persistent="saving || !!action">
    <v-card v-if="target" title="隔离邮件审阅">
      <v-card-text>
        <v-alert type="warning" variant="tonal" class="mb-4">内容可能包含恶意链接或附件。这里仅显示纯文本，不加载外部图片，不执行邮件 HTML。</v-alert>
        <dl class="review-facts"><div><dt>发件人</dt><dd>{{ target.sender }}</dd></div><div><dt>收件人</dt><dd>{{ target.recipients.join('、') }}</dd></div><div><dt>隔离原因</dt><dd>{{ target.reason }}</dd></div><div><dt>来源 IP</dt><dd>{{ target.remoteAddress }}</dd></div><div><dt>审核状态</dt><dd>{{ label(target.status) }}</dd></div><div><dt>审核人 / 时间</dt><dd>{{ target.reviewedBy || '—' }} / {{ date(target.reviewedAt) }}</dd></div><div><dt>审核备注</dt><dd>{{ target.reviewNote || '—' }}</dd></div></dl>
        <v-progress-linear v-if="detailLoading" indeterminate />
        <v-alert v-if="detailError" type="error" variant="tonal">{{ detailError }}<v-btn variant="text" @click="review(target)">重新读取</v-btn></v-alert>
        <h3 class="my-4">原始 MIME 内容</h3>
        <v-alert v-if="detail?.truncated" type="info" variant="tonal" class="mb-3">邮件较大，页面仅显示前 256K 字符。请下载完整 .eml 文件继续审阅。</v-alert>
        <pre v-if="detail" class="quarantine-raw">{{ detail.content }}</pre>
      </v-card-text>
      <v-card-actions><v-btn :disabled="saving || downloadLoading" @click="detailOpen = false">关闭</v-btn><v-btn prepend-icon="mdi-download" :loading="downloadLoading" :disabled="detailLoading || saving" @click="downloadRaw">下载完整 .eml</v-btn><template v-if="canManage && target.status === 'PendingReview'"><v-btn color="error" :disabled="detailLoading || saving || downloadLoading" @click="requestAction('delete')">删除</v-btn><v-btn color="primary" :disabled="!detail || detailLoading || saving || downloadLoading" @click="requestAction('release')">放行邮件</v-btn></template></v-card-actions>
    </v-card>
  </v-dialog>
  <v-dialog :model-value="!!action" max-width="560" persistent>
    <v-card :title="`${actionLabel}隔离邮件`"><v-card-text>
      <v-alert v-if="actionError" type="error" variant="tonal" class="mb-4">{{ actionError }}</v-alert>
      <p class="mb-4">{{ action === 'release' ? '放行将邮件投递到原收件人的收件箱；请确认内容安全。' : '删除后该记录不能再放行；审核记录仍会保留。' }}</p>
      <v-textarea v-model="note" label="审核备注（可选）" rows="3" :readonly="saving" class="mb-6" />
      <v-text-field v-model="confirmation" :label="`输入「${actionLabel}」确认`" :readonly="saving" autocomplete="off" />
    </v-card-text><v-card-actions><v-btn :disabled="saving" @click="cancelAction">取消</v-btn><v-btn :color="action === 'release' ? 'primary' : 'error'" :loading="saving" :disabled="confirmation !== actionLabel" @click="execute">确认{{ actionLabel }}</v-btn></v-card-actions></v-card>
  </v-dialog>
</template>

<style scoped>
.review-facts { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin: 0 0 24px; }
.review-facts dt { color: var(--text-muted); font-size: 12px; }
.review-facts dd { margin: 4px 0 0; overflow-wrap: anywhere; }
.quarantine-raw { max-height: 42vh; overflow: auto; padding: 18px; background: #f5f7fa; border: 1px solid var(--border); border-radius: 8px; white-space: pre-wrap; overflow-wrap: anywhere; font: 12px/1.6 monospace; }
td { max-width: 340px; overflow-wrap: anywhere; }
@media (max-width: 800px) { .review-facts { grid-template-columns: 1fr; } .table-toolbar { flex-wrap: wrap; } }
</style>
