<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { api, jsonBody } from '../lib/api'
import { availableInvitationRoles, invitationStatus, type ClusterInvitation } from '../lib/clusterInvitations'

const props = defineProps<{ nodes: { role: string }[]; writable: boolean }>()
const invitations = ref<ClusterInvitation[]>([])
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const formError = ref('')
const notice = ref('')
const creating = ref(false)
const selectedRole = ref<'Standby' | 'Witness'>('Standby')
const issued = ref<ClusterInvitation | null>(null)
const reveal = ref(false)
const copied = ref(false)
const acknowledged = ref(false)
const revoking = ref<ClusterInvitation | null>(null)
const confirmation = ref('')
const now = ref(Date.now())
const roles = computed(() => availableInvitationRoles(props.nodes))
const roleName = (role: string) => role === 'Standby' ? '副本节点' : role === 'Witness' ? '见证节点' : role
const status = (item: ClusterInvitation) => invitationStatus(item, now.value)
let timer: ReturnType<typeof globalThis.setInterval> | undefined
async function load() {
  if (loading.value) return
  loading.value = true; error.value = ''
  try {
    // Keep credentials out of the persistent list even if the response changes.
    invitations.value = (await api<ClusterInvitation[]>('/cluster/invitations')).map(item => ({ ...item, token: null }))
    now.value = Date.now()
  } catch (e) { error.value = e instanceof Error ? e.message : '邀请列表加载失败' }
  finally { loading.value = false }
}
function openCreate() {
  const first = roles.value[0]
  if (!props.writable || !first) return
  selectedRole.value = first.value; formError.value = ''; creating.value = true
}
async function create() {
  if (saving.value || !props.writable || !roles.value.some(role => role.value === selectedRole.value)) return
  saving.value = true; formError.value = ''
  try {
    issued.value = await api<ClusterInvitation>('/cluster/invitations', { method: 'POST', body: jsonBody({ role: selectedRole.value }) })
    creating.value = false; reveal.value = false; copied.value = false; acknowledged.value = false
    await load()
  } catch (e) { formError.value = e instanceof Error ? e.message : '邀请生成失败' }
  finally { saving.value = false }
}
async function copyToken() {
  if (!issued.value?.token) return
  formError.value = ''
  try { await globalThis.navigator.clipboard.writeText(issued.value.token); copied.value = true }
  catch { formError.value = '无法访问剪贴板，请显示令牌后手动复制。' }
}
function closeToken() {
  if (!acknowledged.value || saving.value) return
  issued.value = null; reveal.value = false; formError.value = ''
}
function requestRevoke(item: ClusterInvitation) { revoking.value = item; confirmation.value = ''; formError.value = '' }
async function revoke() {
  if (!revoking.value || confirmation.value !== revoking.value.id || saving.value || !props.writable) return
  saving.value = true; formError.value = ''
  try {
    await api('/cluster/invitations/' + encodeURIComponent(revoking.value.id), { method: 'DELETE' })
    const id = revoking.value.id
    invitations.value = invitations.value.map(item => item.id === id ? { ...item, revoked: true } : item)
    revoking.value = null; notice.value = '邀请已撤销，不能再用于加入节点。'
  } catch (e) { formError.value = e instanceof Error ? e.message : '撤销失败' }
  finally { saving.value = false }
}
function beforeUnload(event: { preventDefault: () => void; returnValue: unknown }) {
  if (issued.value?.token) { event.preventDefault(); event.returnValue = '' }
}
onBeforeRouteLeave(() => !issued.value?.token || globalThis.confirm('令牌只展示一次。离开后无法重新查看，确定离开？'))
onMounted(() => { void load(); timer = globalThis.setInterval(() => { now.value = Date.now() }, 1000); globalThis.addEventListener('beforeunload', beforeUnload) })
onBeforeUnmount(() => { globalThis.clearInterval(timer); issued.value = null; globalThis.removeEventListener('beforeunload', beforeUnload) })
</script>

<template>
  <section class="panel table-panel mt-5">
    <div class="panel-head"><div><h2>节点加入邀请</h2><p>有效期 15 分钟，令牌仅展示一次；请通过可信渠道交给节点管理员。</p></div>
      <div class="d-flex ga-2"><v-btn variant="text" :loading="loading" :disabled="saving" @click="load">刷新邀请</v-btn><v-btn color="primary" :disabled="loading || saving || !writable || !roles.length" @click="openCreate">生成加入邀请</v-btn></div>
    </div>
    <v-alert v-if="!writable" type="info" variant="tonal">当前节点不可写，请在可写主节点管理邀请。</v-alert>
    <v-alert v-else-if="!roles.length" type="info" variant="tonal">副本和见证角色均已有节点，不能继续生成对应角色的邀请。</v-alert>
    <v-alert v-if="error" type="error" variant="tonal">{{ error }} 列表可能未更新，请重试。</v-alert>
    <v-alert v-if="notice" type="success" variant="tonal" closable @click:close="notice = ''">{{ notice }}</v-alert>
    <v-table><thead><tr><th>邀请编号</th><th>角色</th><th>到期时间（本地）</th><th>状态</th><th>操作</th></tr></thead><tbody>
      <tr v-for="item in invitations" :key="item.id"><td class="invitation-id">{{ item.id }}</td><td>{{ roleName(item.role) }}</td><td>{{ new Date(item.expiresAt).toLocaleString('zh-CN', { hour12: false }) }}</td><td>{{ status(item) }}</td><td><v-btn v-if="status(item) === '待使用'" variant="text" color="error" :disabled="!writable || saving" :aria-label="'撤销邀请 ' + item.id" @click="requestRevoke(item)">撤销</v-btn><span v-else>—</span></td></tr>
      <tr v-if="!invitations.length && !loading"><td colspan="5">{{ error ? '邀请未能加载' : '尚无邀请记录' }}</td></tr>
    </tbody></v-table>
    <details class="join-help"><summary>新节点如何加入？</summary><ol><li>在目标 Linux 节点准备配置文件、空应用数据库和空邮件存储目录；不要在已有业务数据的节点直接执行。</li><li>确认目标节点能够访问主节点的 TLS 集群传输端点（tls://主机:端口）（不是管理后台地址）。</li><li>在邀请到期前，由节点管理员运行加入命令。替换下方占位参数，不要原样执行；不要将真实令牌发到日志或工单。</li></ol><pre>mailserver cluster join --config &lt;配置文件路径&gt; --endpoint &lt;主节点tls://主机:端口&gt; --advertise-endpoint &lt;新节点tls://主机:端口&gt; --name &lt;新节点名称&gt; --token &lt;邀请令牌&gt;</pre><p>完成后刷新集群节点列表，确认初始化与复制状态。生成邀请本身不会创建节点。</p></details>
  </section>
  <v-dialog v-model="creating" max-width="520" persistent><v-card title="生成加入邀请"><v-form @submit.prevent="create"><v-card-text><v-alert v-if="formError" type="error" variant="tonal" class="mb-4">{{ formError }}</v-alert><p class="mb-4">令牌可用于加入一个指定角色的节点。请确认接收人有权管理集群。</p><v-select v-model="selectedRole" label="节点角色" :items="roles" :disabled="saving"/></v-card-text><v-card-actions><v-spacer/><v-btn :disabled="saving" @click="creating = false">取消</v-btn><v-btn type="submit" color="primary" :loading="saving" :disabled="saving || !writable || !roles.some(role => role.value === selectedRole)">确认生成</v-btn></v-card-actions></v-form></v-card></v-dialog>
  <v-dialog :model-value="!!issued" max-width="640" persistent><v-card v-if="issued" title="加入邀请已生成"><v-card-text><v-alert type="warning" variant="tonal" class="mb-4">令牌仅本次展示。关闭后无法找回；遗失时请撤销该邀请并重新生成。</v-alert><p class="mb-3">{{ roleName(issued.role) }} · 到期：{{ new Date(issued.expiresAt).toLocaleString('zh-CN', { hour12: false }) }}</p><v-alert v-if="!issued.token" type="error" variant="tonal">服务端未返回令牌，请撤销此邀请后重试。</v-alert><v-text-field v-else :model-value="issued.token" label="一次性加入令牌" :type="reveal ? 'text' : 'password'" readonly autocomplete="off"><template #append-inner><v-btn :aria-label="reveal ? '隐藏令牌' : '显示令牌'" :icon="reveal ? 'mdi-eye-off-outline' : 'mdi-eye-outline'" variant="text" size="small" @click="reveal = !reveal"/></template></v-text-field><v-btn v-if="issued.token" variant="outlined" @click="copyToken">{{ copied ? '已复制令牌' : '复制令牌' }}</v-btn><v-alert v-if="formError" type="error" variant="tonal" class="mt-3">{{ formError }}</v-alert><v-checkbox v-model="acknowledged" label="我已妥善保存令牌，或知道关闭后需要重新生成" hide-details/></v-card-text><v-card-actions><v-spacer/><v-btn color="primary" :disabled="!acknowledged || saving" @click="closeToken">关闭令牌窗口</v-btn></v-card-actions></v-card></v-dialog>
  <v-dialog :model-value="!!revoking" max-width="560" persistent><v-card v-if="revoking" title="撤销加入邀请"><v-form @submit.prevent="revoke"><v-card-text><p class="mb-3">撤销后，持有此令牌的人将无法再用它加入。已加入节点不会被移除。</p><p class="invitation-id mb-3">输入邀请编号确认：{{ revoking.id }}</p><v-alert v-if="formError" type="error" variant="tonal" class="mb-3">{{ formError }}</v-alert><v-text-field v-model="confirmation" label="确认邀请编号" :readonly="saving"/></v-card-text><v-card-actions><v-spacer/><v-btn :disabled="saving" @click="revoking = null">取消</v-btn><v-btn type="submit" color="error" :loading="saving" :disabled="saving || confirmation !== revoking.id || !writable">确认撤销</v-btn></v-card-actions></v-form></v-card></v-dialog>
</template>

<style scoped>
.invitation-id { overflow-wrap: anywhere; }
.join-help { padding: 20px; color: var(--text-secondary); font-size: 14px; line-height: 1.8; }
.join-help summary { cursor: pointer; color: var(--text); font-weight: 600; }
.join-help ol { padding-left: 24px; margin-top: 12px; }
.join-help pre { white-space: pre-wrap; overflow-wrap: anywhere; background: var(--bg-app); padding: 12px; margin-block: 12px; }
</style>
