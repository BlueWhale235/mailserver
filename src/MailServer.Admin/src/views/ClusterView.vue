<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import PageHeader from '../components/PageHeader.vue'
import ClusterInvitations from '../components/ClusterInvitations.vue'
import ClusterLifecycleActions from '../components/ClusterLifecycleActions.vue'
import { api } from '../lib/api'
import { useAuthStore } from '../stores/auth'
interface ClusterNode {
  id: string; name: string; role: string; status: string; endpoint: string
  version: string; lastHeartbeatAt: string; appliedSequence: number; safeSequence: number
  initializationProgress: number; lastError?: string
}
interface Overview {
  configuration: { id: string; name: string; primaryNodeId: string } | null
  health: { alive: boolean; writable: boolean; nodeId: string; role: string; hasQuorum: boolean; lastSequence: number; replicationLag: number }
  recoveryRequired: boolean
  transport: { enabled: boolean; isListening: boolean; status: string; advertiseEndpoint: string; lastError?: string }
  nodes: ClusterNode[]
  cursors: { nodeId: string; lastAppliedSequence: number; syncStatus: string; lastError?: string; lastSuccessAt?: string }[]
}
const auth = useAuthStore()
const data = ref<Overview | null>(null)
const busy = ref(false)
const saving = ref(false)
const error = ref('')
const notice = ref('')
const actionError = ref('')
const confirmation = ref('')
type ClusterAction = 'resync' | 'repair' | 'promote' | 'remove-node' | 'replication-reset' | 'delete-cluster'
const action = ref<{ kind: ClusterAction; name: string; id: string } | null>(null)
const canRead = computed(() => auth.can('cluster.read'))
const canManage = computed(() => auth.can('cluster.manage'))
const roles: Record<string, string> = { Primary: '主节点', Standby: '副本节点', Witness: '见证节点' }
const transportStates: Record<string, string> = { disabled: '已停用', listening: '监听中', failed: '启动失败', starting: '启动中', stopped: '已停止', unknown: '未知' }
const statuses: Record<string, string> = { Unknown: '未知', Healthy: '健康', Unreachable: '无法连接', Synchronizing: '同步中', ReadOnly: '只读', Idle: '空闲', Throttled: '限流中', Failed: '失败', CatchingUp: '追赶中' }
const actionTitles: Record<ClusterAction, string> = {
  'resync': '确认重新同步',
  'repair': '确认修复本机传输',
  'promote': '确认提升为主节点',
  'remove-node': '确认移除集群节点',
  'replication-reset': '确认重置复制游标',
  'delete-cluster': '确认删除集群配置',
}
const actionHints: Record<ClusterAction, string> = {
  'resync': '此操作会重置副本复制进度并请求全量快照，可能增加主节点负载。',
  'repair': '此操作会重新配置本机集群传输，请确认维护窗口及节点配置。',
  'promote': '提升后本机将通过见证节点获取主节点租约并开始接受写入。请先确认原主节点已停止服务，否则可能产生双主与数据分叉。',
  'remove-node': '该节点会从集群配置中移除。请先确认节点已下线，被移除的节点必须重新初始化才能再次加入。',
  'replication-reset': '复制游标会重置为 0，下一次同步将从最早日志开始重放。',
  'delete-cluster': '删除后本机退出集群协调，副本数据会保留但不再同步。该操作不可自动撤销。',
}
function date(value?: string) { return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '尚无记录' }
async function load() {
  if (busy.value) return
  busy.value = true; error.value = ''
  try { data.value = await api<Overview>('/cluster') }
  catch (e) { error.value = e instanceof Error ? e.message : '集群信息加载失败' }
  finally { busy.value = false }
}
function request(kind: ClusterAction, name: string, id: string) {
  action.value = { kind, name, id }; confirmation.value = ''; actionError.value = ''
}
function showNotice(message: string) { notice.value = message }
async function execute() {
  if (!action.value || saving.value || confirmation.value !== action.value.name || !canManage.value) return
  saving.value = true; actionError.value = ''
  try {
    const current = action.value
    if (current.kind === 'resync') {
      await api(`/cluster/nodes/${encodeURIComponent(current.id)}/resync`, { method: 'POST' })
      notice.value = '重新同步请求已受理；请刷新查看复制进度。'
    } else if (current.kind === 'repair') {
      await api('/cluster/transport/repair', { method: 'POST' })
      notice.value = '传输修复请求已受理；请刷新确认监听状态。'
    } else if (current.kind === 'promote') {
      await api('/cluster/promote', { method: 'POST' })
      notice.value = '本机已提升为主节点，请确认其他节点已只读。'
    } else if (current.kind === 'remove-node') {
      await api(`/cluster/nodes/${encodeURIComponent(current.id)}`, { method: 'DELETE' })
      notice.value = `节点「${current.name}」已从集群移除，请在基础设施层确认其已下线。`
    } else if (current.kind === 'replication-reset') {
      await api(`/cluster/replication/${encodeURIComponent(current.id)}/reset`, { method: 'POST' })
      notice.value = `节点「${current.name}」的复制游标已重置为 0。`
    } else {
      await api(`/cluster?confirmName=${encodeURIComponent(current.name)}`, { method: 'DELETE' })
      notice.value = '集群配置已删除，本机不再参与集群协调。'
    }
    action.value = null
    await load()
  } catch (e) { actionError.value = e instanceof Error ? e.message : '操作失败，请重试' }
  finally { saving.value = false }
}
onMounted(() => { if (canRead.value) void load() })
</script>

<template>
  <PageHeader title="集群" description="查看实际节点状态、复制水位与集群传输">
    <ClusterLifecycleActions v-if="canManage && data" :configuration="data.configuration" :health="data.health" :recovery-required="data.recoveryRequired" :nodes="data.nodes" @reload="load" @notice="showNotice"/>
    <v-btn v-if="canRead" variant="outlined" :loading="busy" :disabled="saving" prepend-icon="mdi-refresh" @click="load">刷新</v-btn>
  </PageHeader>
  <v-alert v-if="!canRead" type="info" variant="tonal">当前账号没有查看集群的权限。</v-alert>
  <template v-else>
    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}{{ data ? ' 下方保留上次读取的数据，可能已过期。' : '' }}</v-alert>
    <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>
    <v-progress-linear v-if="busy" indeterminate class="mb-4"/>
    <template v-if="data">
      <v-alert v-if="!data.configuration" type="info" variant="tonal" class="mb-4">当前节点尚未配置集群。下方为本机运行信息，不代表存在其他节点。集群创建及新节点加入需要配置集群端点。</v-alert>
      <v-alert v-if="data.recoveryRequired" type="error" variant="tonal" class="mb-4">此节点处于紧急恢复锁定状态，写入和协议服务应保持只读。只有在所有远端节点已被可靠隔离并移除后，才可恢复主节点写入。</v-alert>
      <section class="metric-grid cluster-metrics">
        <article><span>集群</span><strong class="metric-name">{{ data.configuration?.name || '未配置集群' }}</strong><small>当前节点：{{ data.health.nodeId }}</small></article>
        <article><span>本机角色</span><strong class="metric-name">{{ roles[data.health.role] || data.health.role }}</strong><small>{{ data.health.alive ? '本机存活' : '本机健康异常' }} · {{ data.health.writable ? '可写入' : '只读' }}</small></article>
        <article><span>仲裁状态</span><strong class="metric-name">{{ !data.configuration ? '不适用' : data.health.hasQuorum ? '已取得仲裁' : '未取得仲裁' }}</strong><small>最新日志序号：{{ data.health.lastSequence }}</small></article>
        <article><span>复制落后量</span><strong>{{ data.health.replicationLag }}</strong><small>日志序号差，不是网络延迟</small></article>
      </section>
      <section class="panel mb-5">
        <div class="panel-head"><div><h2>集群传输</h2><p>{{ data.transport.enabled ? data.transport.isListening ? '正在监听' : '未监听' : '未启用' }} · {{ transportStates[data.transport.status.toLowerCase()] || data.transport.status }}</p></div>
          <v-btn v-if="canManage && data.configuration && data.health.role === 'Primary' && !data.recoveryRequired" variant="outlined" :disabled="busy || saving" @click="request('repair', data.configuration.name, data.health.nodeId)">修复本机传输</v-btn>
        </div>
        <p class="cluster-endpoint">对外端点：{{ data.transport.advertiseEndpoint || '未配置' }}</p>
        <v-alert v-if="data.transport.lastError" type="warning" variant="tonal">{{ data.transport.lastError }}</v-alert>
      </section>
      <section class="panel table-panel">
        <div class="panel-head"><div><h2>集群节点</h2><p>共 {{ data.nodes.length }} 个节点；状态以最近一次服务端读取为准。</p></div></div>
        <v-table><thead><tr><th>节点 / 端点</th><th>角色</th><th>状态</th><th>复制水位</th><th>最近心跳</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="node in data.nodes" :key="node.id">
              <td><b>{{ node.name }}</b><small>{{ node.id }} · {{ node.version || '版本未知' }}</small><small class="cluster-endpoint">{{ node.endpoint || '未配置端点' }}</small></td>
              <td>{{ roles[node.role] || node.role }}{{ node.id === data.health.nodeId ? '（本机）' : '' }}</td>
              <td>{{ statuses[node.status] || node.status }}<small v-if="node.status === 'Synchronizing'">初始化 {{ node.initializationProgress }}%</small><small v-if="node.lastError" class="text-error">{{ node.lastError }}</small></td>
              <td>{{ node.appliedSequence }}<small>安全水位 {{ node.safeSequence }}</small></td>
              <td>{{ date(node.lastHeartbeatAt) }}</td>
              <td class="node-actions">
              <v-btn v-if="canManage && node.role === 'Standby'" variant="text" :disabled="busy || saving || node.status === 'Synchronizing'" :aria-label="'重新同步 ' + node.name" @click="request('resync', node.name, node.id)">重新同步</v-btn>
              <v-btn v-if="canManage && node.id !== data.health.nodeId" variant="text" :disabled="busy || saving" :aria-label="'移除节点 ' + node.name" @click="request('remove-node', node.name, node.id)">移除</v-btn>
              <span v-if="!canManage || (node.role !== 'Standby' && node.id === data.health.nodeId)">—</span>
            </td>
            </tr>
            <tr v-if="!data.nodes.length"><td colspan="6">尚无集群节点记录。</td></tr>
          </tbody>
        </v-table>
      </section>
      <section class="panel table-panel mt-5">
        <div class="panel-head"><div><h2>复制进度</h2><p>查看各节点的复制状态与最近成功时间。</p></div></div>
        <v-table><thead><tr><th>节点</th><th>状态</th><th>已应用序号</th><th>最近成功</th><th>错误详情</th><th>操作</th></tr></thead>
          <tbody><tr v-for="cursor in data.cursors" :key="cursor.nodeId"><td>{{ cursor.nodeId }}</td><td>{{ statuses[cursor.syncStatus] || cursor.syncStatus }}</td><td>{{ cursor.lastAppliedSequence }}</td><td>{{ date(cursor.lastSuccessAt) }}</td><td>{{ cursor.lastError || '—' }}</td><td><v-btn v-if="canManage" variant="text" :disabled="busy || saving" @click="request('replication-reset', cursor.nodeId, cursor.nodeId)">重置游标</v-btn></td></tr>
            <tr v-if="!data.cursors.length"><td colspan="6">尚无复制记录。</td></tr></tbody>
        </v-table>
      </section>
      <ClusterInvitations v-if="canManage && data.configuration" :nodes="data.nodes" :writable="data.health.writable && data.health.role === 'Primary'"/>
    </template>
  </template>
  <v-dialog :model-value="!!action" max-width="560" persistent>
    <v-card v-if="action" :title="actionTitles[action.kind]">
      <v-form @submit.prevent="execute"><v-card-text>
        <v-alert :type="action.kind === 'delete-cluster' || action.kind === 'remove-node' ? 'error' : 'warning'" variant="tonal" class="mb-4">{{ actionHints[action.kind] }}</v-alert>
        <v-alert v-if="actionError" type="error" variant="tonal" class="mb-4">{{ actionError }}</v-alert>
        <p class="mb-3">请输入完整名称「{{ action.name }}」确认。</p>
        <v-text-field v-model="confirmation" label="确认名称" autocomplete="off" :readonly="saving" autofocus/>
      </v-card-text><v-card-actions><v-spacer/><v-btn :disabled="saving" @click="action = null">取消</v-btn><v-btn type="submit" color="primary" :loading="saving" :disabled="saving || confirmation !== action.name">确认执行</v-btn></v-card-actions></v-form>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.cluster-endpoint { overflow-wrap: anywhere; }
td { max-width: 360px; overflow-wrap: anywhere; }
</style>
