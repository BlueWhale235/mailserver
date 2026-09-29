<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import PageHeader from '../components/PageHeader.vue'
import { api } from '../lib/api'
import { useAuthStore } from '../stores/auth'

interface Operation { id: string; traceId: string; sourceTraceId?: string; name: string; module: string; status: string; startedAt: string; durationMilliseconds: number; processingMilliseconds: number; sqlMilliseconds: number; sqlCount: number; slow: boolean; truncated: boolean; queueWaitMilliseconds?: number; retryBackoffMilliseconds?: number; clockAnomaly?: boolean }
interface Stage { id: string; parentId?: string; name: string; category: string; offsetMilliseconds: number; durationMilliseconds: number; selfMilliseconds: number; status: string; executionMilliseconds?: number; sqlFingerprint?: string; errorType?: string }
interface Detail { unattributedMilliseconds?: number; sql: { fingerprint: string; template: string }[]; operation: Operation; stages: Stage[]; related: Operation[]; sourceAvailable: boolean; relatedTruncated: boolean }
interface Ranking { module: string; name: string; count: number; errors: number; slow: number; totalMilliseconds: number; p50: number; p95: number; p99: number }
interface Summary { rankings: Ranking[]; truncated: boolean; sql: { fingerprint: string; template: string }[]; processes: { processId: string; droppedOperations: number; droppedAggregates: number; truncatedOperations: number; storageUnavailable: boolean; bufferBytes: number; budgetBytes: number }[] }
interface Settings { enabled: boolean; sampleRate: number; apiMilliseconds: number; imapMilliseconds: number; smtpMilliseconds: number; deliveryMilliseconds: number; stageMilliseconds: number; sqlMilliseconds: number; sqlTotalMilliseconds: number; repeatedSqlCount: number; detailDays: number; aggregateDays: number; detailBudgetBytes: number; diagnosticUntil?: string }
const root = '/operations/performance'
const auth = useAuthStore()
const tab = ref('operations'), busy = ref(false), error = ref(''), notice = ref('')
const rows = ref<Operation[]>([]), nextCursor = ref<string | null>(null), detail = ref<Detail | null>(null)
const summary = ref<Summary | null>(null), settings = ref<Settings | null>(null), kind = ref('operation')
const form = ref({ module: '', status: '', traceId: '', messageId: '', deliveryJobId: '', minimumMilliseconds: '', from: '', to: '', slowOnly: true })
const canManage = computed(() => auth.can('performance.manage'))
const fields: { key: keyof Settings; label: string; min: number; max: number; step?: number }[] = [
  { key: 'sampleRate', label: '正常操作详情采样率（0–1）', min: 0, max: 1, step: .01 },
  { key: 'apiMilliseconds', label: 'HTTP 慢操作阈值（ms）', min: 1, max: 3600000 },
  { key: 'imapMilliseconds', label: 'IMAP 慢操作阈值（ms）', min: 1, max: 3600000 },
  { key: 'smtpMilliseconds', label: 'SMTP 慢操作阈值（ms）', min: 1, max: 3600000 },
  { key: 'deliveryMilliseconds', label: '外发慢操作阈值（ms）', min: 1, max: 3600000 },
  { key: 'stageMilliseconds', label: '处理阶段阈值（ms）', min: 1, max: 3600000 },
  { key: 'sqlMilliseconds', label: '单条 SQL 阈值（ms）', min: 1, max: 3600000 },
  { key: 'sqlTotalMilliseconds', label: '累计 SQL 阈值（ms）', min: 1, max: 3600000 },
  { key: 'repeatedSqlCount', label: '重复 SQL 次数阈值', min: 2, max: 1000 },
  { key: 'detailDays', label: '详情保留天数', min: 1, max: 90 },
  { key: 'aggregateDays', label: '聚合保留天数', min: 1, max: 365 },
  { key: 'detailBudgetBytes', label: '详情存储预算（字节）', min: 1048576, max: 10737418240 },
]
const ms = (value: number | undefined) => value == null ? '—' : `${value.toFixed(2)} ms`
const time = (value: string) => new Date(value).toLocaleString()
function query() {
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(form.value)) {
    if (value === '' || value === false) continue
    params.set(key, (key === 'from' || key === 'to') ? new Date(String(value)).toISOString() : String(value))
  }
  return params
}
async function run(action: () => Promise<void>) {
  busy.value = true; error.value = ''; notice.value = ''
  try { await action() } catch (e) { error.value = e instanceof Error ? e.message : '操作失败' }
  finally { busy.value = false }
}
async function search(more = false) {
  await run(async () => {
    const params = query()
    if (more && nextCursor.value) params.set('cursor', nextCursor.value)
    const result = await api<{ items: Operation[]; nextCursor: string | null }>(`${root}/operations?${params}`)
    rows.value = more ? [...rows.value, ...result.items] : result.items; nextCursor.value = result.nextCursor
  })
}
async function open(id: string) { await run(async () => { detail.value = await api<Detail>(`${root}/operations/${id}`) }) }
async function loadSummary() { await run(async () => { const p = query(); p.set('kind', kind.value); summary.value = await api<Summary>(`${root}/summary?${p}`) }) }
async function save() { await run(async () => { settings.value = await api<Settings>(`${root}/settings`, { method: 'PUT', body: JSON.stringify(settings.value) }); notice.value = '已保存，其他进程将在约 10 秒内应用。' }) }
async function diagnostic(enabled: boolean) { await run(async () => { settings.value = await api<Settings>(`${root}/diagnostic`, { method: 'POST', body: JSON.stringify({ enabled }) }); notice.value = enabled ? '已开始 15 分钟全量详情诊断；容量不足时仍会丢弃记录。' : '诊断已停止。' }) }
function bar(stage: Stage) {
  const total = Math.max(1, detail.value?.operation.durationMilliseconds ?? 1)
  return { marginLeft: `${Math.min(100, stage.offsetMilliseconds / total * 100)}%`, width: `${Math.max(.4, Math.min(100, stage.durationMilliseconds / total * 100))}%` }
}
onMounted(async () => { await search(); await run(async () => { settings.value = await api<Settings>(`${root}/settings`) }); await loadSummary() })
</script>

<template>
  <PageHeader title="性能分析" description="从一次操作追踪到各处理阶段与数据库查询" />
  <v-alert v-if="error" type="error" class="mb-4">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" class="mb-4">{{ notice }}</v-alert>
  <v-alert type="info" variant="tonal" class="mb-4">详情保留慢操作、错误及正常采样；排名使用已采集操作的分钟聚合，分位数为直方图近似值。上传、下载等待与队列等待单独统计。</v-alert>
  <v-alert v-if="summary?.processes.some(p => p.droppedOperations || p.droppedAggregates || p.storageUnavailable)" type="warning" class="mb-4">采集存在丢弃或存储故障，当前数据不完整，请查看采集状态。</v-alert>
  <v-tabs v-model="tab" class="mb-4"><v-tab value="operations">慢操作</v-tab><v-tab value="rankings">耗时排名</v-tab><v-tab value="settings">采集设置</v-tab></v-tabs>
  <v-card v-if="tab !== 'settings'" class="pa-4 mb-4">
    <div class="filters">
      <v-text-field v-model="form.from" type="datetime-local" label="开始时间（默认过去 24 小时）" />
      <v-text-field v-model="form.to" type="datetime-local" label="结束时间" />
      <template v-if="tab === 'operations'">
        <v-select v-model="form.module" :items="['', 'smtp', 'imap', 'worker', 'api']" label="模块" />
        <v-select v-model="form.status" :items="['', 'ok', 'error', 'timeout', 'cancelled', 'rejected']" label="状态" />
        <v-text-field v-model="form.traceId" label="追踪 ID" /><v-text-field v-model="form.messageId" label="邮件 ID" /><v-text-field v-model="form.deliveryJobId" label="投递任务 ID" />
        <v-text-field v-model="form.minimumMilliseconds" type="number" min="0" label="最小处理耗时（ms）" />
        <v-checkbox v-model="form.slowOnly" label="只看慢操作" hide-details />
      </template>
      <v-select v-else v-model="kind" :items="[{ title: '操作', value: 'operation' }, { title: '阶段', value: 'stage' }, { title: 'SQL', value: 'sql' }]" label="排名类型" />
    </div>
    <v-btn :loading="busy" @click="tab === 'operations' ? search() : loadSummary()">查询</v-btn>
  </v-card>
  <v-card v-if="tab === 'operations'">
    <v-table><thead><tr><th>时间 / 操作</th><th>状态</th><th>总耗时</th><th>处理耗时</th><th>SQL 次数 / 耗时</th><th>详情</th></tr></thead>
      <tbody><tr v-for="row in rows" :key="row.id"><td>{{ time(row.startedAt) }}<br>{{ row.module }} · {{ row.name }}</td><td>{{ row.status }}{{ row.truncated ? ' · 截断' : '' }}</td><td>{{ ms(row.durationMilliseconds) }}</td><td>{{ ms(row.processingMilliseconds) }}</td><td>{{ row.sqlCount }} / {{ ms(row.sqlMilliseconds) }}</td><td><v-btn variant="text" :disabled="busy" @click="open(row.id)">查看</v-btn></td></tr></tbody>
    </v-table><p v-if="!rows.length" class="pa-5">没有匹配的详情。正常操作可能未被采样，历史记录可能已清理。</p>
    <v-btn v-if="nextCursor" class="ma-4" :loading="busy" @click="search(true)">加载更多</v-btn>
  </v-card>
  <v-card v-if="tab === 'rankings' && summary">
    <v-alert v-if="summary.truncated" type="warning">数据量超出本次查询上限，请缩小时间范围。</v-alert>
    <v-table><thead><tr><th>名称</th><th>次数 / 错误 / 慢</th><th>累计耗时</th><th>P50 ≈</th><th>P95 ≈</th><th>P99 ≈</th></tr></thead><tbody><tr v-for="row in summary.rankings" :key="row.module + row.name"><td class="name">{{ row.module }} · {{ row.name }}<pre v-if="kind === 'sql'">{{ summary.sql.find(s => s.fingerprint === row.name)?.template ?? '模板未保留' }}</pre></td><td>{{ row.count }} / {{ row.errors }} / {{ row.slow }}</td><td>{{ ms(row.totalMilliseconds) }}</td><td>{{ ms(row.p50) }}</td><td>{{ ms(row.p95) }}</td><td>{{ ms(row.p99) }}</td></tr></tbody></v-table>
    <p v-if="!summary.rankings.length" class="pa-5">暂无聚合数据。</p>
  </v-card>
  <v-card v-if="tab === 'settings' && settings" class="pa-5">
    <v-switch v-model="settings.enabled" label="启用性能采集" :disabled="!canManage" />
    <div class="filters"><v-text-field v-for="field in fields" :key="field.key" :model-value="settings[field.key]" :label="field.label" type="number" :min="field.min" :max="field.max" :step="field.step ?? 1" :disabled="!canManage" @update:model-value="settings = { ...settings!, [field.key]: Number($event) }" /></div>
    <v-btn v-if="canManage" :loading="busy" class="mr-3" @click="save">保存设置</v-btn>
    <v-btn v-if="canManage" :disabled="busy || !settings.enabled" class="mr-3" @click="diagnostic(true)">诊断 15 分钟</v-btn>
    <v-btn v-if="canManage && settings.diagnosticUntil" :disabled="busy" @click="diagnostic(false)">停止诊断</v-btn>
    <p class="mt-4">诊断到期时间：{{ settings.diagnosticUntil ? time(settings.diagnosticUntil) : '未开启' }}。启用诊断前请先保存采集设置。</p>
    <h3 class="mt-6">采集状态</h3>
    <p v-for="process in summary?.processes" :key="process.processId" class="mt-3 name">{{ process.processId }}<br>缓冲 {{ process.bufferBytes }} / {{ process.budgetBytes }} 字节 · 丢弃操作 {{ process.droppedOperations }} · 丢弃聚合 {{ process.droppedAggregates }} · 截断 {{ process.truncatedOperations }} · {{ process.storageUnavailable ? '存储故障' : '最近上报正常' }}</p>
  </v-card>
  <v-dialog :model-value="!!detail" max-width="1200" @update:model-value="!$event && (detail = null)">
    <v-card v-if="detail" class="pa-5"><div class="d-flex justify-space-between"><h2>{{ detail.operation.module }} · {{ detail.operation.name }}</h2><v-btn variant="text" @click="detail = null">关闭</v-btn></div>
      <p class="name">追踪 ID：{{ detail.operation.traceId }}</p><p>总耗时 {{ ms(detail.operation.durationMilliseconds) }} · 处理 {{ ms(detail.operation.processingMilliseconds) }} · SQL {{ detail.operation.sqlCount }} 次 / {{ ms(detail.operation.sqlMilliseconds) }}</p>
      <p v-if="detail.operation.queueWaitMilliseconds != null">排队 {{ ms(detail.operation.queueWaitMilliseconds) }} · 本次重试退避 {{ ms(detail.operation.retryBackoffMilliseconds) }}</p>
      <v-alert v-if="detail.operation.truncated || detail.operation.clockAnomaly" type="warning" class="my-3">{{ detail.operation.truncated ? '阶段详情已截断，统计可能不完整。' : '' }}{{ detail.operation.clockAnomaly ? '检测到时钟差异，负等待时间已归零。' : '' }}</v-alert>
      <p class="my-3">未细分耗时 {{ ms(detail.unattributedMilliseconds) }}。横条表示相对开始时间。自身耗时扣除直接子阶段的时间并集；嵌套及并行阶段不能直接相加。SQL 总耗时为客户端执行阶段，读取生命周期另行显示。database.open 表示逻辑打开或从连接池取用，不代表新建物理连接。</p>
      <v-table><thead><tr><th>阶段</th><th class="timeline">时间线</th><th>含子阶段 / 自身</th><th>状态</th></tr></thead><tbody><tr v-for="stage in detail.stages" :key="stage.id"><td class="name">{{ stage.parentId ? '↳ ' : '' }}{{ stage.name }}<small v-if="stage.sqlFingerprint"><br>{{ detail.sql?.find(s => s.fingerprint === stage.sqlFingerprint)?.template ?? stage.sqlFingerprint }}</small></td><td><div class="track"><div class="bar" :class="stage.category" :style="bar(stage)" /></div>{{ ms(stage.offsetMilliseconds) }}</td><td>{{ ms(stage.durationMilliseconds) }} / {{ ms(stage.selfMilliseconds) }}<br><small v-if="stage.executionMilliseconds != null">执行 {{ ms(stage.executionMilliseconds) }}</small></td><td>{{ stage.status }}<br>{{ stage.errorType }}</td></tr></tbody></v-table>
      <p v-if="detail.operation.sourceTraceId && !detail.sourceAvailable" class="mt-3">来源操作未采样、已清理或未记录。</p>
      <h3 class="mt-4">关联操作</h3><v-btn v-for="related in detail.related" :key="related.id" variant="text" :disabled="busy" @click="open(related.id)">{{ related.module }} · {{ related.name }} · {{ time(related.startedAt) }}</v-btn>
      <p v-if="detail.relatedTruncated">关联结果超过 100 条，请使用追踪 ID 或邮件 ID 筛选。</p>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.filters { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 12px; }
.name { overflow-wrap: anywhere; max-width: 420px; } pre { white-space: pre-wrap; font-size: 12px; }
.timeline { min-width: 230px; } .track { width: 100%; background: rgba(128,128,128,.12); height: 12px; overflow: hidden; border-radius: 3px; }
.bar { height: 12px; background: #398571; } .bar.wait { background: #c68b30; } .bar.sql { background: #637db5; }
</style>
