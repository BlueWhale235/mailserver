<script setup lang="ts">
import { computed, ref } from 'vue'
import { api, jsonBody } from '../lib/api'

interface Configuration { name: string; primaryNodeId: string }
interface Health { nodeId: string; role: string; writable: boolean; hasQuorum: boolean }
interface NodeSummary { id: string; name: string; role: string }

const props = defineProps<{
  configuration: Configuration | null
  health: Health
  recoveryRequired: boolean
  nodes: NodeSummary[]
}>()
const emit = defineEmits<{ reload: []; notice: [message: string] }>()
const creating = ref(false)
const recovering = ref(false)
const promoting = ref(false)
const deleting = ref(false)
const saving = ref(false)
const error = ref('')
const clusterName = ref('')
const endpoint = ref('')
const confirmName = ref('')
const nodesFenced = ref(false)
const remoteNodes = computed(() => props.nodes.filter(node => node.id !== props.health.nodeId))
const canRecover = computed(() => props.recoveryRequired && props.configuration
  && props.health.role === 'Primary' && remoteNodes.value.length === 0)

function validTlsEndpoint(value: string) {
  try {
    const parsed = new globalThis.URL(value.trim())
    return parsed.protocol === 'tls:' && !!parsed.hostname && !parsed.username && !parsed.password && !parsed.pathname.replaceAll('/', '') && !parsed.search && !parsed.hash
  } catch { return false }
}
function openCreate() {
  clusterName.value = ''; endpoint.value = ''; error.value = ''; creating.value = true
}
async function createCluster() {
  const name = clusterName.value.trim()
  const advertisedEndpoint = endpoint.value.trim()
  if (!name) { error.value = '请输入集群名称'; return }
  if (name.length > 128) { error.value = '集群名称不能超过 128 个字符'; return }
  if (!validTlsEndpoint(advertisedEndpoint)) { error.value = '请输入有效的 TLS 集群端点，例如 tls://mail-01.example.com:7443'; return }
  if (saving.value) return
  saving.value = true; error.value = ''
  try {
    await api('/cluster', { method: 'POST', body: jsonBody({ name, endpoint: advertisedEndpoint }) })
    creating.value = false
    emit('notice', '集群已创建，本机已成为主节点。请确认集群传输开始监听后再生成加入邀请。')
    emit('reload')
  } catch (e) { error.value = e instanceof Error ? e.message : '集群创建失败' }
  finally { saving.value = false }
}
async function promote() {
  if (promoting.value || saving.value) return
  promoting.value = true; error.value = ''
  try {
    await api('/cluster/promote', { method: 'POST' })
    promoting.value = false
    emit('notice', '本机已提升为主节点；请确认原主节点已停止写入。')
    emit('reload')
  } catch (e) { error.value = e instanceof Error ? e.message : '提升主节点失败' }
  finally { promoting.value = false }
}
function openDelete() {
  confirmName.value = ''; error.value = ''; deleting.value = true
}
async function deleteCluster() {
  if (!props.configuration || confirmName.value !== props.configuration.name || saving.value) return
  saving.value = true; error.value = ''
  try {
    await api(`/cluster?confirmName=${encodeURIComponent(confirmName.value)}`, { method: 'DELETE' })
    deleting.value = false
    emit('notice', '集群配置已删除，本机不再参与集群协调。')
    emit('reload')
  } catch (e) { error.value = e instanceof Error ? e.message : '删除集群失败' }
  finally { saving.value = false }
}
function openRecovery() {
  confirmName.value = ''; nodesFenced.value = false; error.value = ''; recovering.value = true
}
async function recoverPrimary() {
  if (!props.configuration || !canRecover.value || confirmName.value !== props.configuration.name || !nodesFenced.value || saving.value) return
  saving.value = true; error.value = ''
  try {
    await api('/cluster/recover-primary', { method: 'POST', body: jsonBody({ confirmName: confirmName.value, confirmNodesFenced: true }) })
    recovering.value = false
    emit('notice', '主节点写入能力已恢复。被移除的旧节点必须保持隔离，重新初始化后才能加入。')
    emit('reload')
  } catch (e) { error.value = e instanceof Error ? e.message : '主节点恢复失败' }
  finally { saving.value = false }
}
</script>

<template>
  <v-btn v-if="!configuration" color="primary" prepend-icon="mdi-server-plus" @click="openCreate">创建集群</v-btn>
  <v-btn v-else-if="recoveryRequired" color="error" prepend-icon="mdi-alert-octagon-outline" @click="openRecovery">紧急恢复主节点</v-btn>
  <template v-else>
    <v-btn v-if="health.role !== 'Primary'" variant="outlined" prepend-icon="mdi-arrow-up-bold-circle-outline" :loading="promoting" :disabled="saving" @click="promote">提升为主节点</v-btn>
    <v-btn variant="outlined" color="error" prepend-icon="mdi-delete-outline" :disabled="saving" @click="openDelete">删除集群</v-btn>
  
  <v-dialog v-model="deleting" max-width="660" persistent>
    <v-card v-if="configuration" title="删除集群配置">
      <v-form @submit.prevent="deleteCluster"><v-card-text>
        <v-alert type="error" variant="tonal" class="mb-4">删除后本机退出集群协调，管理后台不再展示其他节点。副本数据不会被自动清理，但也不会再同步。该操作不能自动撤销。</v-alert>
        <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>
        <p class="mb-3">输入集群名称「{{ configuration.name }}」确认删除。</p>
        <v-text-field v-model="confirmName" label="确认集群名称" autocomplete="off" :readonly="saving" autofocus />
      </v-card-text><v-card-actions><v-spacer/><v-btn :disabled="saving" @click="deleting = false">取消</v-btn><v-btn type="submit" color="error" :loading="saving" :disabled="saving || confirmName !== configuration.name">确认删除</v-btn></v-card-actions></v-form>
    </v-card>
  </v-dialog>

</template>

  <v-dialog v-model="creating" max-width="600" persistent>
    <v-card title="创建集群">
      <v-form @submit.prevent="createCluster"><v-card-text>
        <v-alert type="info" variant="tonal" class="mb-4">创建后，本机将成为主节点并生成集群 CA 与节点证书。请使用其他节点能够访问的固定 TLS 地址。</v-alert>
        <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>
        <v-text-field v-model="clusterName" label="集群名称" placeholder="例如 production-mail" maxlength="128" counter :readonly="saving" autofocus class="mb-2"/>
        <v-text-field v-model="endpoint" label="本机集群端点" placeholder="tls://mail-01.example.com:7443" hint="这是节点间传输地址，不是管理后台 URL；必须使用 tls://。" persistent-hint :readonly="saving"/>
      </v-card-text><v-card-actions><v-spacer/><v-btn :disabled="saving" @click="creating = false">取消</v-btn><v-btn type="submit" color="primary" :loading="saving" :disabled="saving">确认创建</v-btn></v-card-actions></v-form>
    </v-card>
  </v-dialog>

  <v-dialog v-model="recovering" max-width="660" persistent>
    <v-card v-if="configuration" title="紧急恢复主节点写入">
      <v-form @submit.prevent="recoverPrimary"><v-card-text>
        <v-alert type="error" variant="tonal" class="mb-4">仅在原主节点仍保留完整数据，且所有远端节点已关机、断网或被其他方式隔离时使用。错误操作可能造成双主和数据分叉。</v-alert>
        <v-alert v-if="remoteNodes.length" type="warning" variant="tonal" class="mb-4">仍有远端节点记录：{{ remoteNodes.map(node => node.name + '（' + node.id + '）').join('、') }}。必须先强制移除，并在基础设施层确认隔离。</v-alert>
        <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>
        <p class="mb-3">输入集群名称「{{ configuration.name }}」确认。</p>
        <v-text-field v-model="confirmName" label="确认集群名称" autocomplete="off" :readonly="saving"/>
        <v-checkbox v-model="nodesFenced" :disabled="saving" label="我确认所有远端节点均已关机、断网或被可靠隔离，旧节点不会继续提供服务"/>
      </v-card-text><v-card-actions><v-spacer/><v-btn :disabled="saving" @click="recovering = false">取消</v-btn><v-btn type="submit" color="error" :loading="saving" :disabled="saving || !canRecover || confirmName !== configuration.name || !nodesFenced">确认紧急恢复</v-btn></v-card-actions></v-form>
    </v-card>
  </v-dialog>
</template>
