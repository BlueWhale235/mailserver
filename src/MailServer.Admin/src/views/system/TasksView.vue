<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ApiError, api, jsonBody } from '../../lib/api'
import PageHeader from '../../components/PageHeader.vue'
import StatusPill from '../../components/StatusPill.vue'
import { usePermission } from '../../composables/usePermission'

interface ScheduledTask {
  id: string
  type: string
  name: string
  isSystem: boolean
  isEnabled: boolean
  isClusterTask: boolean
  intervalMinutes: number
  timeoutSeconds: number
  batchSize: number
  maximumAttempts: number
  status: 'Idle' | 'Running' | 'Paused' | 'Failed'
  nextRunAt: string
  lastRunAt?: string
}

interface TaskExecution {
  id: string
  scheduledTaskId: string
  status: 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'
  nodeId: string
  startedAt: string
  completedAt?: string
  processedCount: number
  releasedBytes: number
  attempt: number
  error?: string
}

interface EditForm {
  enabled: boolean
  intervalMinutes: number
  timeoutSeconds: number
  batchSize: number
}

const { can } = usePermission()

const tasks = ref<ScheduledTask[]>([])
const executions = ref<TaskExecution[]>([])
const loading = ref(false)
const error = ref('')
const notice = ref('')

const editOpen = ref(false)
const editTarget = ref<ScheduledTask | null>(null)
const editForm = ref<EditForm>({ enabled: true, intervalMinutes: 60, timeoutSeconds: 600, batchSize: 100 })
const editOriginal = ref<EditForm | null>(null)
const editLoading = ref(false)
const editError = ref('')
const discardEditOpen = ref(false)

const runTarget = ref<ScheduledTask | null>(null)
const runLoading = ref(false)

const executionDetail = ref<TaskExecution | null>(null)
const executionStatusFilter = ref<'All' | TaskExecution['status']>('All')
const executionTaskFilter = ref('All')

const enabledCount = computed(() => tasks.value.filter(task => task.isEnabled).length)
const runningCount = computed(() => tasks.value.filter(task => task.status === 'Running').length)
const attentionCount = computed(() => tasks.value.filter(task => task.status === 'Failed' || !task.isEnabled).length)
const editDirty = computed(() => Boolean(editOriginal.value) && JSON.stringify(editOriginal.value) !== JSON.stringify(editForm.value))
const editValid = computed(() =>
  Number.isInteger(editForm.value.intervalMinutes) && editForm.value.intervalMinutes >= 1 && editForm.value.intervalMinutes <= 525600
  && Number.isInteger(editForm.value.timeoutSeconds) && editForm.value.timeoutSeconds >= 10 && editForm.value.timeoutSeconds <= 86400
  && Number.isInteger(editForm.value.batchSize) && editForm.value.batchSize >= 1 && editForm.value.batchSize <= 1000)

const executionTaskItems = computed(() => [
  { title: '全部任务', value: 'All' },
  ...tasks.value.map(task => ({ title: task.name, value: task.id })),
])

const filteredExecutions = computed(() => executions.value.filter(execution =>
  (executionStatusFilter.value === 'All' || execution.status === executionStatusFilter.value)
  && (executionTaskFilter.value === 'All' || execution.scheduledTaskId === executionTaskFilter.value)))

function taskStatus(task: ScheduledTask) {
  if (task.status === 'Running') return { label: '运行中', tone: 'warning' as const }
  if (task.status === 'Failed') return { label: '需要处理', tone: 'danger' as const }
  if (!task.isEnabled || task.status === 'Paused') return { label: '已暂停', tone: 'neutral' as const }
  return { label: '正常', tone: 'success' as const }
}

function executionStatus(status: TaskExecution['status']) {
  if (status === 'Succeeded') return { label: '成功', tone: 'success' as const }
  if (status === 'Failed') return { label: '失败', tone: 'danger' as const }
  if (status === 'Cancelled') return { label: '已取消', tone: 'neutral' as const }
  return { label: '运行中', tone: 'warning' as const }
}

async function loadTasks() {
  loading.value = true
  error.value = ''
  try {
    const [taskList, executionList] = await Promise.all([
      api<ScheduledTask[]>('/tasks'),
      api<TaskExecution[]>('/tasks/executions?take=50'),
    ])
    tasks.value = taskList
    executions.value = executionList
  } catch (cause) {
    tasks.value = []
    executions.value = []
    error.value = cause instanceof Error ? cause.message : '计划任务加载失败'
  } finally {
    loading.value = false
  }
}

function openEdit(task: ScheduledTask) {
  const form = {
    enabled: task.isEnabled,
    intervalMinutes: task.intervalMinutes,
    timeoutSeconds: task.timeoutSeconds,
    batchSize: task.batchSize,
  }
  editTarget.value = task
  editForm.value = { ...form }
  editOriginal.value = { ...form }
  editError.value = ''
  editOpen.value = true
}

function requestCloseEdit() {
  if (editLoading.value) return
  if (editDirty.value) {
    discardEditOpen.value = true
    return
  }
  editOpen.value = false
}

function discardEdit() {
  discardEditOpen.value = false
  editOpen.value = false
  editTarget.value = null
  editOriginal.value = null
}

async function saveEdit() {
  if (!editTarget.value || !editValid.value || !editDirty.value) return
  editLoading.value = true
  editError.value = ''
  try {
    const targetName = editTarget.value.name
    await api(`/tasks/${editTarget.value.id}`, {
      method: 'PUT',
      body: jsonBody(editForm.value),
    })
    editOpen.value = false
    editOriginal.value = null
    notice.value = `${targetName} 的调度配置已保存`
    await loadTasks()
  } catch (cause) {
    editError.value = cause instanceof ApiError || cause instanceof Error ? cause.message : '更新任务失败'
  } finally {
    editLoading.value = false
  }
}

function requestRun(task: ScheduledTask) {
  runTarget.value = task
}

async function confirmRun() {
  if (!runTarget.value) return
  runLoading.value = true
  error.value = ''
  try {
    const targetName = runTarget.value.name
    const result = await api<{ executionId: string }>(`/tasks/${runTarget.value.id}/run`, { method: 'POST' })
    runTarget.value = null
    notice.value = `${targetName} 已执行，记录 ${result.executionId.slice(0, 8)}`
    await loadTasks()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '触发任务失败'
  } finally {
    runLoading.value = false
  }
}

function bytes(value: number) {
  if (!value) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1)
  return `${(value / 1024 ** index).toFixed(index > 2 ? 1 : 0)} ${units[index]}`
}

function formatInterval(minutes: number) {
  if (minutes % 43200 === 0) return `${minutes / 43200} 个月`
  if (minutes % 1440 === 0) return `${minutes / 1440} 天`
  if (minutes % 60 === 0) return `${minutes / 60} 小时`
  return `${minutes} 分钟`
}

function executionTaskName(execution: TaskExecution) {
  return tasks.value.find(task => task.id === execution.scheduledTaskId)?.name ?? '未知任务'
}

function executionDuration(execution: TaskExecution) {
  if (!execution.completedAt) return '进行中'
  const milliseconds = Math.max(0, new Date(execution.completedAt).getTime() - new Date(execution.startedAt).getTime())
  if (milliseconds < 1000) return `${milliseconds} ms`
  return `${(milliseconds / 1000).toFixed(milliseconds < 10_000 ? 1 : 0)} s`
}

onMounted(loadTasks)
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/system">系统</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>计划任务</span>
  </div>

  <PageHeader title="计划任务" description="统一管理维护、清理、配额与集群检查任务">
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="loading" @click="loadTasks">刷新</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>

  <div class="task-summary" aria-label="计划任务状态摘要">
    <span><b>{{ tasks.length }}</b>任务总数</span>
    <span><b>{{ enabledCount }}</b>已启用</span>
    <span :class="{ 'is-active': runningCount > 0 }"><b>{{ runningCount }}</b>运行中</span>
    <span :class="{ 'is-danger': attentionCount > 0 }"><b>{{ attentionCount }}</b>需要关注</span>
  </div>

  <v-card flat class="panel table-panel">
    <div class="panel-head px-5 pt-5">
      <div><h2>任务配置</h2><p>手动执行前会再次确认；运行中的任务不能修改或重复触发</p></div>
    </div>
    <v-table>
      <thead><tr><th>任务</th><th>状态</th><th>执行周期</th><th>限制</th><th>下次运行</th><th>上次运行</th><th></th></tr></thead>
      <tbody>
        <tr v-if="loading"><td colspan="7"><v-progress-linear indeterminate color="primary" /></td></tr>
        <tr v-else-if="!tasks.length"><td colspan="7" class="text-center py-10 text-medium-emphasis">没有可用的计划任务</td></tr>
        <tr v-for="task in tasks" v-else :key="task.id">
          <td><b>{{ task.name }}</b><small><code>{{ task.type }}</code> · {{ task.isClusterTask ? '集群主节点' : '当前节点' }}</small></td>
          <td><StatusPill v-bind="taskStatus(task)" /></td>
          <td>{{ formatInterval(task.intervalMinutes) }}</td>
          <td><span class="limit-main">{{ task.batchSize }} 项/批</span><small>超时 {{ formatInterval(Math.ceil(task.timeoutSeconds / 60)) }}</small></td>
          <td>{{ task.isEnabled ? new Date(task.nextRunAt).toLocaleString('zh-CN') : '已暂停调度' }}</td>
          <td>{{ task.lastRunAt ? new Date(task.lastRunAt).toLocaleString('zh-CN') : '从未运行' }}</td>
          <td class="text-no-wrap text-end">
            <v-btn
              v-if="can('task.manage')"
              size="small"
              variant="text"
              icon="mdi-play"
              :disabled="task.status === 'Running'"
              :aria-label="task.status === 'Running' ? `${task.name} 正在运行` : `立即执行 ${task.name}`"
              @click="requestRun(task)"
            />
            <v-btn
              v-if="can('task.manage')"
              size="small"
              variant="text"
              icon="mdi-cog-outline"
              :disabled="task.status === 'Running'"
              :aria-label="task.status === 'Running' ? `${task.name} 正在运行，不能编辑` : `编辑 ${task.name}`"
              @click="openEdit(task)"
            />
          </td>
        </tr>
      </tbody>
    </v-table>
  </v-card>

  <v-card flat class="panel table-panel mt-4">
    <div class="history-head">
      <div><h2>执行历史</h2><p>最近 50 次执行；失败记录可展开查看完整原因</p></div>
      <div class="history-filters">
        <v-select v-model="executionTaskFilter" :items="executionTaskItems" label="任务" hide-details density="compact" />
        <v-select
          v-model="executionStatusFilter"
          :items="[{ title: '全部状态', value: 'All' }, { title: '运行中', value: 'Running' }, { title: '成功', value: 'Succeeded' }, { title: '失败', value: 'Failed' }, { title: '已取消', value: 'Cancelled' }]"
          label="状态"
          hide-details
          density="compact"
        />
      </div>
    </div>
    <v-table>
      <thead><tr><th>任务</th><th>状态</th><th>节点</th><th>处理结果</th><th>开始时间</th><th>耗时</th><th></th></tr></thead>
      <tbody>
        <tr v-if="!filteredExecutions.length"><td colspan="7" class="text-center py-10 text-medium-emphasis">没有符合条件的执行记录</td></tr>
        <tr v-for="execution in filteredExecutions" v-else :key="execution.id">
          <td><b>{{ executionTaskName(execution) }}</b><small>第 {{ execution.attempt }} 次尝试</small></td>
          <td><StatusPill v-bind="executionStatus(execution.status)" /></td>
          <td><code>{{ execution.nodeId }}</code></td>
          <td>{{ execution.processedCount }} 项<small v-if="execution.releasedBytes">释放 {{ bytes(execution.releasedBytes) }}</small></td>
          <td>{{ new Date(execution.startedAt).toLocaleString('zh-CN') }}</td>
          <td>{{ executionDuration(execution) }}</td>
          <td class="text-end"><v-btn size="small" variant="text" icon="mdi-text-box-search-outline" :aria-label="`查看 ${executionTaskName(execution)} 执行详情`" @click="executionDetail = execution" /></td>
        </tr>
      </tbody>
    </v-table>
  </v-card>

  <v-dialog v-model="editOpen" max-width="560" persistent>
    <v-card>
      <v-card-title class="d-flex align-center gap-2"><v-icon color="primary">mdi-cog-outline</v-icon> 编辑任务 · {{ editTarget?.name }}</v-card-title>
      <v-card-text>
        <v-alert v-if="editError" type="error" variant="tonal" density="compact" class="mb-4">{{ editError }}</v-alert>
        <v-switch v-model="editForm.enabled" label="启用自动调度" color="primary" :disabled="editLoading" />
        <v-text-field v-model.number="editForm.intervalMinutes" label="执行间隔（分钟）" type="number" min="1" max="525600" hint="允许 1 分钟至 1 年" persistent-hint :disabled="editLoading" />
        <v-text-field v-model.number="editForm.timeoutSeconds" label="单次超时（秒）" type="number" min="10" max="86400" hint="允许 10 秒至 24 小时" persistent-hint :disabled="editLoading" />
        <v-text-field v-model.number="editForm.batchSize" label="每批处理数量" type="number" min="1" max="1000" hint="允许 1 至 1000 项" persistent-hint :disabled="editLoading" />
        <v-alert v-if="!editValid" type="warning" variant="tonal" density="compact">请将所有数值调整到允许范围内。</v-alert>
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="editLoading" @click="requestCloseEdit">取消</v-btn><v-btn color="primary" :loading="editLoading" :disabled="!editValid || !editDirty" @click="saveEdit">保存配置</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog v-model="discardEditOpen" max-width="460" persistent>
    <v-card>
      <v-card-title>放弃未保存修改？</v-card-title>
      <v-card-text>任务配置已经发生变化。放弃后，本次修改不会保存。</v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" @click="discardEditOpen = false">继续编辑</v-btn><v-btn color="error" variant="tonal" @click="discardEdit">放弃修改</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="Boolean(runTarget)" max-width="540" persistent @update:model-value="value => { if (!value && !runLoading) runTarget = null }">
    <v-card>
      <v-card-title class="d-flex align-center gap-2"><v-icon color="warning">mdi-play-circle-outline</v-icon> 立即执行任务</v-card-title>
      <v-card-text>
        <v-alert type="warning" variant="tonal" class="mb-4">任务会在主节点立即开始，可能占用数据库、存储或集群资源。</v-alert>
        <p class="confirm-copy">确认立即执行 <strong>{{ runTarget?.name }}</strong>？</p>
        <dl class="run-summary"><div><dt>运行范围</dt><dd>{{ runTarget?.isClusterTask ? '集群主节点' : '当前节点' }}</dd></div><div><dt>单次上限</dt><dd>{{ runTarget?.batchSize }} 项</dd></div><div><dt>超时</dt><dd>{{ runTarget ? formatInterval(Math.ceil(runTarget.timeoutSeconds / 60)) : '—' }}</dd></div></dl>
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="runLoading" @click="runTarget = null">取消</v-btn><v-btn color="warning" :loading="runLoading" @click="confirmRun">确认立即执行</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="Boolean(executionDetail)" max-width="620" @update:model-value="value => { if (!value) executionDetail = null }">
    <v-card>
      <v-card-title class="d-flex align-center gap-2"><v-icon color="primary">mdi-text-box-search-outline</v-icon> 执行详情</v-card-title>
      <v-card-text v-if="executionDetail">
        <div class="detail-grid">
          <span>任务</span><b>{{ executionTaskName(executionDetail) }}</b>
          <span>执行编号</span><code>{{ executionDetail.id }}</code>
          <span>节点</span><code>{{ executionDetail.nodeId }}</code>
          <span>状态</span><StatusPill v-bind="executionStatus(executionDetail.status)" />
          <span>开始时间</span><b>{{ new Date(executionDetail.startedAt).toLocaleString('zh-CN') }}</b>
          <span>耗时</span><b>{{ executionDuration(executionDetail) }}</b>
          <span>处理结果</span><b>{{ executionDetail.processedCount }} 项，释放 {{ bytes(executionDetail.releasedBytes) }}</b>
        </div>
        <v-alert v-if="executionDetail.error" type="error" variant="tonal" class="mt-5"><strong>失败原因：</strong>{{ executionDetail.error }}</v-alert>
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn color="primary" @click="executionDetail = null">关闭</v-btn></v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.task-summary { display: flex; align-items: center; gap: 0; margin-bottom: 16px; overflow: hidden; border: 1px solid var(--border); border-radius: 10px; background: #fff; }
.task-summary span { display: flex; align-items: baseline; gap: 7px; min-width: 140px; padding: 13px 18px; border-right: 1px solid var(--border); color: var(--text-muted); font-size: 12px; }
.task-summary span:last-child { border-right: 0; }
.task-summary b { color: var(--text); font-size: 19px; }
.task-summary .is-active b { color: #b76400; }
.task-summary .is-danger b { color: #c92a3a; }
.limit-main { display: block; }
.history-head { display: flex; align-items: flex-end; justify-content: space-between; gap: 24px; padding: 20px; }
.history-head h2 { margin: 0 0 3px; font-size: 16px; }
.history-head p { margin: 0; color: var(--text-muted); font-size: 12px; }
.history-filters { display: grid; grid-template-columns: 220px 150px; gap: 10px; }
.confirm-copy { margin: 0 0 18px; color: var(--text-secondary); }
.run-summary { display: grid; grid-template-columns: repeat(3, 1fr); margin: 0; overflow: hidden; border: 1px solid var(--border); border-radius: 8px; }
.run-summary div { padding: 11px 13px; border-right: 1px solid var(--border); }
.run-summary div:last-child { border-right: 0; }
.run-summary dt { color: var(--text-muted); font-size: 11px; }
.run-summary dd { margin: 4px 0 0; color: var(--text); font-size: 13px; font-weight: 700; }
.detail-grid { display: grid; grid-template-columns: 110px minmax(0, 1fr); align-items: center; gap: 12px 18px; }
.detail-grid > span { color: var(--text-muted); font-size: 12px; }
.detail-grid code { overflow-wrap: anywhere; }
@media (max-width: 760px) {
  .task-summary { align-items: stretch; flex-wrap: wrap; }
  .task-summary span { flex: 1 1 50%; border-bottom: 1px solid var(--border); }
  .history-head { align-items: stretch; flex-direction: column; }
  .history-filters { grid-template-columns: 1fr; }
  .run-summary { grid-template-columns: 1fr; }
  .run-summary div { border-right: 0; border-bottom: 1px solid var(--border); }
}
</style>
