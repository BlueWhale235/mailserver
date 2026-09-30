<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import PageHeader from '../components/PageHeader.vue'
import { useAuthStore } from '../stores/auth'
import { api, apiDownload } from '../lib/api'

interface MailRow {
  id: string; internetMessageId?: string; envelopeFrom: string; receivedAt: string; size: number
  sourceType?: string; authenticatedAccountName?: string; sourceIp?: string
  mailboxCopies: number; recycledCopies: number; deliveryJobs: number; quarantinedCopies: number
}
interface Copy { id: string; accountAddress: string; mailboxName: string; uid: number; deletedAt?: string }
interface Detail { message: MailRow; originalRecipients: string[]; mailboxCopies: Copy[]; deliveryRecipients: string[]; quarantineRecipients: string[]; originalRecipientCount: number; mailboxCopyCount: number; deliveryRecipientCount: number; quarantineRecipientCount: number }
interface Content { fromHeader: string; subject: string; headers: string; text: string; truncated: boolean; attachments: string[] }
interface Delivery { id: string; recipient: string; status: string; attempts: number; lastError?: string }
interface Attempt { attemptNumber: number; succeeded: boolean; remoteHost?: string; smtpStatusCode?: string; response?: string }
interface Process { processId: string; instanceName: string; roles: string[]; state: string; message?: string; lastSeenAt: string; estimatedCacheBytes: number; usage?: { activeDeliveries: number; activeSmtp: number; activeImap: number; activeSmtpMessages?: number; deliveryLimit: number; smtpLimit: number; smtpMessageLimit?: number; imapLimit: number; serviceStates?: Record<string, string> } }
interface Task { id: string; type: string; name: string; status: string; lastRunAt?: string }
interface Execution { scheduledTaskId: string; status: string; startedAt: string; processedCount: number; error?: string }

const auth = useAuthStore()
const canMail = computed(() => auth.can('operations.mail.read'))
const canContent = computed(() => canMail.value && auth.can('operations.mail.content.read'))
const canStatus = computed(() => auth.can('settings.read'))
const canMaintenance = computed(() => auth.can('task.read'))
const tab = ref(canMail.value ? 'mail' : canStatus.value ? 'status' : 'maintenance')
const form = ref({ messageId: '', sender: '', recipient: '', authenticatedAccount: '', from: '', to: '', deliveryStatus: '', recycled: '' })
const rows = ref<MailRow[]>([])
const total = ref(0)
const page = ref(1)
const busy = ref(false)
const error = ref('')
const detail = ref<Detail | null>(null)
const content = ref<Content | null>(null)
const deliveries = ref<Delivery[]>([])
const attempts = ref<Attempt[]>([])
const attemptTotal = ref(0)
const attemptPage = ref(1)
const selectedJob = ref('')
const deliveryTotal = ref(0)
const deliveryPage = ref(1)
const detailError = ref('')
const status = ref<Process[]>([])
const tasks = ref<Task[]>([])
const executions = ref<Execution[]>([])
const pageCount = computed(() => Math.max(1, Math.ceil(total.value / 50)))
const relevantTasks = computed(() => tasks.value.filter(task => ['storage.integrity-check', 'cleanup.recycle', 'quota.reconcile'].includes(task.type)))
const stateNames: Record<string, string> = { applied: '已应用', applying: '应用中', failed: '暂时无法应用', unreported: '上报过期' }
const sourceNames: Record<string, string> = { smtp: 'SMTP 收发', bounce: '系统退信', plugin: '插件发信', system: '系统生成', quarantine: '隔离邮件', 'imap-append': '客户端存入' }

function params(values: Record<string, string | number>) {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(values)) if (String(value).trim()) query.set(key, String(value))
  return query.toString()
}
async function search(nextPage = 1) {
  if (!canMail.value) return
  busy.value = true; error.value = ''; page.value = nextPage
  try {
    const filters = { ...form.value, from: form.value.from ? new Date(form.value.from).toISOString() : '',
      to: form.value.to ? new Date(form.value.to).toISOString() : '' }
    const result = await api<{ items: MailRow[]; totalCount: number }>(`/operations/messages?${params({ ...filters, page: nextPage, pageSize: 50 })}`)
    rows.value = result.items; total.value = result.totalCount
  } catch (e) { error.value = e instanceof Error ? e.message : '查询失败' }
  finally { busy.value = false }
}
async function loadDeliveries(id: string, nextPage = 1) {
  const result = await api<{ items: Delivery[]; totalCount: number }>(`/operations/messages/${id}/deliveries?page=${nextPage}&pageSize=50`)
  deliveries.value = result.items; deliveryTotal.value = result.totalCount; deliveryPage.value = nextPage
}
async function loadMoreRelated(kind: 'original' | 'delivery' | 'quarantine' | 'copies') {
  if (!detail.value) return
  const id = detail.value.message.id
  const current = kind === 'copies' ? detail.value.mailboxCopies.length
    : kind === 'original' ? detail.value.originalRecipients.length
      : kind === 'delivery' ? detail.value.deliveryRecipients.length : detail.value.quarantineRecipients.length
  const route = kind === 'copies' ? 'copies' : `recipients?kind=${kind}&`
  const separator = kind === 'copies' ? '?' : ''
  try {
    const result = await api<{ items: string[] | Copy[] }>(`/operations/messages/${id}/${route}${separator}page=${Math.floor(current / 50) + 1}&pageSize=50`)
    if (kind === 'copies') detail.value.mailboxCopies.push(...result.items as Copy[])
    else if (kind === 'original') detail.value.originalRecipients.push(...result.items as string[])
    else if (kind === 'delivery') detail.value.deliveryRecipients.push(...result.items as string[])
    else detail.value.quarantineRecipients.push(...result.items as string[])
  } catch (e) { detailError.value = e instanceof Error ? e.message : '关联记录不可用' }
}
async function openDetail(id: string) {
  detail.value = null; content.value = null; detailError.value = ''; attempts.value = []; selectedJob.value = ''
  try {
    detail.value = await api<Detail>(`/operations/messages/${id}`)
    await loadDeliveries(id)
  } catch (e) { detailError.value = e instanceof Error ? e.message : '无法加载邮件详情' }
}
async function showAttempts(jobId: string, nextPage = 1) {
  if (!detail.value) return
  selectedJob.value = jobId
  try {
    const result = await api<{ items: Attempt[]; totalCount: number }>(`/operations/messages/${detail.value.message.id}/deliveries/${jobId}/attempts?page=${nextPage}&pageSize=50`)
    attempts.value = result.items; attemptTotal.value = result.totalCount; attemptPage.value = nextPage
  }
  catch (e) { detailError.value = e instanceof Error ? e.message : '投递尝试记录不可用' }
}
async function preview() {
  if (!detail.value || !canContent.value) return
  detailError.value = ''
  try { content.value = await api<Content>(`/operations/messages/${detail.value.message.id}/content`) }
  catch (e) { detailError.value = e instanceof Error ? e.message : '原文不可用' }
}
async function downloadRaw() {
  if (!detail.value || !canContent.value) return
  detailError.value = ''
  try {
    const file = await apiDownload(`/operations/messages/${detail.value.message.id}/raw`)
    const url = URL.createObjectURL(file.blob)
    const link = document.createElement('a'); link.href = url; link.download = file.fileName; link.click()
    globalThis.setTimeout(() => URL.revokeObjectURL(url), 30_000)
  } catch (e) { detailError.value = e instanceof Error ? e.message : '原文不可用' }
}
async function loadStatus() {
  if (!canStatus.value) return
  try { status.value = (await api<{ processes: Process[] }>('/settings/resources/status')).processes }
  catch (e) { error.value = e instanceof Error ? e.message : '服务状态不可用' }
}
async function loadMaintenance() {
  if (!canMaintenance.value) return
  try {
    tasks.value = await api<Task[]>('/tasks')
    executions.value = await api<Execution[]>('/tasks/executions?take=50')
  } catch (e) { error.value = e instanceof Error ? e.message : '维护信息不可用' }
}
function selectTab(value: string) {
  tab.value = value; error.value = ''
  if (value === 'status') void loadStatus()
  if (value === 'maintenance') void loadMaintenance()
}
function toggleDetail(open: boolean) { if (!open) detail.value = null }
onMounted(() => {
  if (tab.value === 'mail') void search()
  if (tab.value === 'status') void loadStatus()
  if (tab.value === 'maintenance') void loadMaintenance()
})
</script>

<template>
  <PageHeader title="运维中心" description="查询现存邮件、服务状态和维护检查结果" />
  <v-btn v-if="auth.can('performance.read')" to="/operations/performance" prepend-icon="mdi-chart-timeline-variant" class="mb-4">性能分析</v-btn>
  <v-tabs :model-value="tab" class="mb-5" @update:model-value="selectTab(String($event))">
    <v-tab v-if="canMail" value="mail">邮件查询</v-tab>
    <v-tab v-if="canStatus" value="status">服务状态</v-tab>
    <v-tab v-if="canMaintenance" value="maintenance">维护检查</v-tab>
  </v-tabs>
  <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>

  <section v-if="tab === 'mail' && canMail">
    <v-card class="mb-5" title="查找邮件">
      <v-card-text>
        <div class="filters">
          <v-text-field v-model="form.messageId" label="Message-ID" placeholder="可粘贴 Message-ID: <…>" hide-details />
          <v-text-field v-model="form.sender" label="信封发件人" hide-details />
          <v-text-field v-model="form.recipient" label="收件人" hide-details />
          <v-text-field v-model="form.authenticatedAccount" label="实际认证账号" hide-details />
          <v-text-field v-model="form.from" type="datetime-local" label="起始时间" hide-details />
          <v-text-field v-model="form.to" type="datetime-local" label="结束时间" hide-details />
          <v-select v-model="form.deliveryStatus" label="投递状态" :items="[{ title: '全部', value: '' }, ...['Pending', 'Processing', 'Delivered', 'Deferred', 'Failed'].map(value => ({ title: value, value }))]" hide-details />
          <v-select v-model="form.recycled" label="回收站" :items="[{ title: '全部', value: '' }, { title: '包含已删除副本', value: 'true' }, { title: '包含正常副本', value: 'false' }]" hide-details />
        </div>
        <v-btn color="primary" class="mt-4" :loading="busy" @click="search(1)">查询</v-btn>
        <p class="text-medium-emphasis mt-3">默认查询最近 7 天；精确 Message-ID 查询覆盖全部现存记录。彻底清理后的邮件无法查询。</p>
      </v-card-text>
    </v-card>
    <v-card title="查询结果">
      <v-table><thead><tr><th>接收时间</th><th>Message-ID</th><th>信封发件人</th><th>来源</th><th>副本 / 投递 / 隔离</th><th></th></tr></thead>
        <tbody><tr v-for="row in rows" :key="row.id">
          <td>{{ new Date(row.receivedAt).toLocaleString() }}</td><td class="identifier">{{ row.internetMessageId || '未记录' }}</td>
          <td>{{ row.envelopeFrom || '空发件人' }}</td><td>{{ row.sourceType ? sourceNames[row.sourceType] || row.sourceType : '未记录' }}</td>
          <td>{{ row.mailboxCopies }} / {{ row.deliveryJobs }} / {{ row.quarantinedCopies }} <span v-if="row.recycledCopies">（回收站 {{ row.recycledCopies }}）</span></td>
          <td><v-btn size="small" variant="text" @click="openDetail(row.id)">查看</v-btn></td>
        </tr></tbody></v-table>
      <v-card-text v-if="!rows.length">没有找到现存邮件记录。</v-card-text>
      <v-card-actions><span>共 {{ total }} 封</span><v-spacer /><v-btn :disabled="page <= 1" @click="search(page - 1)">上一页</v-btn><span>{{ page }} / {{ pageCount }}</span><v-btn :disabled="page >= pageCount" @click="search(page + 1)">下一页</v-btn></v-card-actions>
    </v-card>
  </section>

  <section v-if="tab === 'status' && canStatus">
    <v-btn variant="outlined" class="mb-4" @click="loadStatus">刷新状态</v-btn>
    <v-alert v-if="!status.length" type="info" variant="tonal">尚未收到服务上报。</v-alert>
    <v-card v-for="process in status" :key="process.processId" class="mb-3">
      <v-card-title>{{ process.instanceName }} · {{ process.roles.join(' / ').toUpperCase() }} · {{ stateNames[process.state] || process.state }}</v-card-title>
      <v-card-text>最后上报：{{ new Date(process.lastSeenAt).toLocaleString() }} · 缓存估算：{{ process.estimatedCacheBytes }} 字节
        <div v-if="process.usage">活动投递 {{ process.usage.activeDeliveries }} / {{ process.usage.deliveryLimit }} · SMTP 连接 {{ process.usage.activeSmtp }} / {{ process.usage.smtpLimit }}<template v-if="process.usage.smtpMessageLimit"> · 活跃邮件处理 {{ process.usage.activeSmtpMessages }} / {{ process.usage.smtpMessageLimit }}</template> · IMAP 连接 {{ process.usage.activeImap }} / {{ process.usage.imapLimit }}</div>
        <div v-for="[role, state] in Object.entries(process.usage?.serviceStates || {})" :key="role">{{ role.toUpperCase() }}：{{ state }}</div>
        <div v-if="process.message">{{ process.message }}</div>
      </v-card-text>
    </v-card>
  </section>

  <section v-if="tab === 'maintenance' && canMaintenance">
    <v-btn variant="outlined" class="mb-4" @click="loadMaintenance">刷新检查结果</v-btn>
    <v-card v-for="task in relevantTasks" :key="task.id" class="mb-3">
      <v-card-title>{{ task.name }} · {{ task.status }}</v-card-title>
      <v-card-text>上次运行：{{ task.lastRunAt ? new Date(task.lastRunAt).toLocaleString() : '尚未运行' }}
        <div v-if="executions.find(item => item.scheduledTaskId === task.id)" class="mt-2">最近结果：{{ executions.find(item => item.scheduledTaskId === task.id)?.status }} · 处理 {{ executions.find(item => item.scheduledTaskId === task.id)?.processedCount }} 项</div>
      </v-card-text>
    </v-card>
    <v-btn :to="'/system/tasks'" variant="text">查看计划任务</v-btn>
    <v-btn v-if="auth.can('audit.read')" :to="'/system/audit'" variant="text">查看审计日志</v-btn>
  </section>

  <v-dialog :model-value="Boolean(detail)" max-width="1000" scrollable @update:model-value="toggleDetail">
    <v-card v-if="detail" title="邮件详情">
      <v-card-text>
        <v-alert v-if="detailError" type="warning" variant="tonal" class="mb-3">{{ detailError }}</v-alert>
        <dl class="detail-list">
          <div><dt>Message-ID</dt><dd>{{ detail.message.internetMessageId || '未记录' }}</dd></div>
          <div><dt>信封发件人</dt><dd>{{ detail.message.envelopeFrom || '空发件人' }}</dd></div>
          <div><dt>实际认证账号</dt><dd>{{ detail.message.authenticatedAccountName || '未记录' }}</dd></div>
          <div><dt>来源 IP</dt><dd>{{ detail.message.sourceIp || '未记录' }}</dd></div>
          <div><dt>原始收件人</dt><dd>{{ detail.originalRecipients.join(', ') || '历史邮件未记录' }}</dd></div>
          <div><dt>投递目标</dt><dd>{{ detail.deliveryRecipients.join(', ') || '无外部投递' }}</dd></div>
          <div><dt>隔离收件人</dt><dd>{{ detail.quarantineRecipients.join(', ') || '无' }}</dd></div>
        </dl>
        <v-btn v-if="detail.originalRecipientCount > detail.originalRecipients.length" size="small" @click="loadMoreRelated('original')">更多原始收件人</v-btn>
        <v-btn v-if="detail.deliveryRecipientCount > detail.deliveryRecipients.length" size="small" @click="loadMoreRelated('delivery')">更多投递目标</v-btn>
        <v-btn v-if="detail.quarantineRecipientCount > detail.quarantineRecipients.length" size="small" @click="loadMoreRelated('quarantine')">更多隔离收件人</v-btn>
        <h3 class="mt-4">邮箱副本</h3>
        <p v-for="copy in detail.mailboxCopies" :key="copy.id">{{ copy.accountAddress }} / {{ copy.mailboxName }} / UID {{ copy.uid }} · {{ copy.deletedAt ? '回收站' : '正常' }}</p>
        <v-btn v-if="detail.mailboxCopyCount > detail.mailboxCopies.length" size="small" @click="loadMoreRelated('copies')">更多邮箱副本</v-btn>
        <h3 class="mt-4">投递记录</h3>
        <p v-for="delivery in deliveries" :key="delivery.id">{{ delivery.recipient }} · {{ delivery.status }} · 尝试 {{ delivery.attempts }} 次 {{ delivery.lastError || '' }} <v-btn size="small" variant="text" @click="showAttempts(delivery.id)">查看尝试</v-btn></p>
        <div v-if="selectedJob"><p v-for="attempt in attempts" :key="attempt.attemptNumber">第 {{ attempt.attemptNumber }} 次 · {{ attempt.succeeded ? '成功' : '失败' }} · {{ attempt.remoteHost || '无远端主机' }} · {{ attempt.smtpStatusCode || '' }} {{ attempt.response || '' }}</p></div>
        <v-btn v-if="selectedJob && attemptPage > 1" size="small" @click="showAttempts(selectedJob, attemptPage - 1)">上一页尝试</v-btn>
        <v-btn v-if="selectedJob && attemptTotal > attemptPage * 50" size="small" @click="showAttempts(selectedJob, attemptPage + 1)">下一页尝试</v-btn>
        <v-btn v-if="deliveryPage > 1" size="small" @click="loadDeliveries(detail.message.id, deliveryPage - 1)">上一页投递记录</v-btn>
        <v-btn v-if="deliveryTotal > deliveryPage * 50" size="small" @click="loadDeliveries(detail.message.id, deliveryPage + 1)">下一页投递记录</v-btn>
        <template v-if="content">
          <h3 class="mt-4">邮件头 From：{{ content.fromHeader || '未记录' }}</h3>
          <p>主题：{{ content.subject || '无主题' }}</p>
          <p>附件：{{ content.attachments.join(', ') || '无' }}</p>
          <pre class="content-preview">{{ content.text }}</pre>
          <p v-if="content.truncated">预览已截断至 256 KiB，可下载原文核查。</p>
          <details><summary>完整邮件头</summary><pre class="content-preview">{{ content.headers }}</pre></details>
        </template>
      </v-card-text>
      <v-card-actions><v-btn v-if="canContent" @click="preview">预览内容</v-btn><v-btn v-if="canContent" @click="downloadRaw">下载原文</v-btn><v-spacer /><v-btn @click="detail = null">关闭</v-btn></v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.filters { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }
.identifier { max-width: 260px; overflow-wrap: anywhere; }
.detail-list { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.detail-list dt { color: var(--text-muted); }
.detail-list dd { margin: 0; overflow-wrap: anywhere; }
.content-preview { white-space: pre-wrap; overflow-wrap: anywhere; max-height: 420px; overflow: auto; }
@media (max-width: 700px) { .filters, .detail-list { grid-template-columns: 1fr; } }
</style>
