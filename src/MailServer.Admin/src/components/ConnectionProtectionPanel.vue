<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { api, jsonBody } from '../lib/api'
import { useAuthStore } from '../stores/auth'

interface LoginPolicy { windowMinutes: number; maximumFailures: number; blockMinutes: number }
interface Quota { perMinute: number; burst: number; maximumConcurrent: number }
interface ProtectionSettings {
  enabled: boolean
  smtpLogin: LoginPolicy
  imapLogin: LoginPolicy
  smtpInbound: Quota
  smtpSubmission: Quota
  imap: Quota
  maximumTrackedSources: number
  tlsHandshakeSeconds: number
  preAuthenticationSeconds: number
}
const defaults: ProtectionSettings = {
  enabled: true,
  smtpLogin: { windowMinutes: 15, maximumFailures: 5, blockMinutes: 30 },
  imapLogin: { windowMinutes: 15, maximumFailures: 5, blockMinutes: 30 },
  smtpInbound: { perMinute: 120, burst: 30, maximumConcurrent: 16 },
  smtpSubmission: { perMinute: 60, burst: 20, maximumConcurrent: 10 },
  imap: { perMinute: 60, burst: 20, maximumConcurrent: 20 },
  maximumTrackedSources: 4096,
  tlsHandshakeSeconds: 10,
  preAuthenticationSeconds: 120,
}
const auth = useAuthStore()
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const notice = ref('')
const saved = ref<ProtectionSettings | null>(null)
const form = ref<ProtectionSettings>(globalThis.structuredClone(defaults))
const dirty = computed(() => saved.value !== null && JSON.stringify(saved.value) !== JSON.stringify(form.value))
interface ProcessStatus { instanceName: string; roles: string; lastSeenAt: string; state: 'applied' | 'applying' | 'error' | 'offline'; trackedSources: number }
const processes = ref<ProcessStatus[]>([])
const statusUnavailable = ref(false)
const statusLabels: Record<ProcessStatus['state'], string> = {
  applied: '已应用', applying: '应用中', error: '暂时无法应用', offline: '上报已过期',
}
let statusTimer: ReturnType<typeof globalThis.setInterval> | undefined
const loginGroups = [
  { key: 'smtpLogin' as const, label: 'SMTP 发信登录' },
  { key: 'imapLogin' as const, label: 'IMAP 登录' },
]
const connectionGroups = [
  { key: 'smtpInbound' as const, label: '普通 SMTP 收信' },
  { key: 'smtpSubmission' as const, label: 'SMTP 认证发信' },
  { key: 'imap' as const, label: 'IMAP' },
]
async function load() {
  if (!auth.can('firewall.read') || saving.value) return
  loading.value = true; error.value = ''
  try {
    const result = await api<ProtectionSettings>('/security/connection-protection')
    saved.value = globalThis.structuredClone(result)
    form.value = globalThis.structuredClone(result)
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '连接防护设置加载失败' }
  finally { loading.value = false }
  await loadStatus()
}
async function loadStatus() {
  if (!auth.can('firewall.read')) return
  try {
    const result = await api<{ processes: ProcessStatus[] }>('/security/connection-protection/status')
    processes.value = result.processes
    statusUnavailable.value = false
  } catch { statusUnavailable.value = true }
}
function undo() { if (saved.value) form.value = globalThis.structuredClone(saved.value) }
function restoreDefaults() { form.value = globalThis.structuredClone(defaults) }
function validate(): string | null {
  const fields: Array<[number, number, number]> = [
    [form.value.maximumTrackedSources, 128, 65536],
    [form.value.tlsHandshakeSeconds, 1, 120],
    [form.value.preAuthenticationSeconds, 1, 3600],
  ]
  for (const policy of [form.value.smtpLogin, form.value.imapLogin])
    fields.push([policy.windowMinutes, 1, 1440], [policy.maximumFailures, 1, 1000], [policy.blockMinutes, 1, 10080])
  for (const quota of [form.value.smtpInbound, form.value.smtpSubmission, form.value.imap])
    fields.push([quota.perMinute, 1, 100000], [quota.burst, 1, 100000], [quota.maximumConcurrent, 1, 10000])
  return fields.every(([value, min, max]) => Number.isSafeInteger(value) && value >= min && value <= max)
    ? null : '请输入允许范围内的正整数。'
}
async function save() {
  if (!auth.can('firewall.manage') || saving.value || !dirty.value) return
  error.value = validate() ?? ''
  if (error.value) return
  saving.value = true; notice.value = ''
  try {
    const result = await api<ProtectionSettings>('/security/connection-protection', {
      method: 'PUT', body: jsonBody(form.value),
    })
    saved.value = globalThis.structuredClone(result)
    form.value = globalThis.structuredClone(result)
    notice.value = '连接防护设置已保存，将在各邮件服务下次配置检查时生效。'
    await loadStatus()
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '保存失败' }
  finally { saving.value = false }
}
onBeforeRouteLeave(() => !dirty.value || globalThis.confirm('连接防护设置尚未保存，确定离开？'))
onMounted(() => {
  void load()
  statusTimer = globalThis.setInterval(() => void loadStatus(), 5000)
})
onBeforeUnmount(() => { if (statusTimer) globalThis.clearInterval(statusTimer) })
</script>

<template>
  <section v-if="auth.can('firewall.read')" class="panel mt-5 pa-5">
    <div class="d-flex align-center justify-space-between mb-3">
      <div><h2>连接与登录保护</h2><p class="text-body-2">限制重复登录和单个 IP 占用的连接。普通收信不会因 SMTP 登录失败而封禁。</p></div>
      <v-btn variant="text" :loading="loading" :disabled="dirty || saving" @click="load">刷新</v-btn>
    </div>
    <v-alert v-if="error" type="error" variant="tonal" class="mb-3">{{ error }}</v-alert>
    <v-alert v-if="notice" type="success" variant="tonal" class="mb-3">{{ notice }}</v-alert>
    <template v-if="saved">
      <v-switch v-model="form.enabled" label="启用连接与登录保护" :disabled="!auth.can('firewall.manage')" color="primary"/>
      <h3 class="text-subtitle-1 mb-2">登录失败封禁</h3>
      <div v-for="group in loginGroups" :key="group.key" class="mb-3">
        <div class="font-weight-medium mb-2">{{ group.label }}</div>
        <div class="d-flex ga-3 flex-wrap">
          <v-text-field v-model.number="form[group.key].windowMinutes" type="number" min="1" max="1440" label="统计窗口（分钟）" :readonly="!auth.can('firewall.manage')"/>
          <v-text-field v-model.number="form[group.key].maximumFailures" type="number" min="1" max="1000" label="失败次数" :readonly="!auth.can('firewall.manage')"/>
          <v-text-field v-model.number="form[group.key].blockMinutes" type="number" min="1" max="10080" label="封禁时间（分钟）" :readonly="!auth.can('firewall.manage')"/>
        </div>
      </div>
      <h3 class="text-subtitle-1 mb-2">每 IP 连接限制</h3>
      <div v-for="group in connectionGroups" :key="group.key" class="mb-3">
        <div class="font-weight-medium mb-2">{{ group.label }}</div>
        <div class="d-flex ga-3 flex-wrap">
          <v-text-field v-model.number="form[group.key].perMinute" type="number" min="1" label="每分钟额度" :readonly="!auth.can('firewall.manage')"/>
          <v-text-field v-model.number="form[group.key].burst" type="number" min="1" label="突发容量" :readonly="!auth.can('firewall.manage')"/>
          <v-text-field v-model.number="form[group.key].maximumConcurrent" type="number" min="1" label="同时连接" :readonly="!auth.can('firewall.manage')"/>
        </div>
      </div>
      <h3 class="text-subtitle-1 mb-2">记录与超时</h3>
      <div class="d-flex ga-3 flex-wrap">
        <v-text-field v-model.number="form.maximumTrackedSources" type="number" min="128" max="65536" label="最多记录 IP 数" :readonly="!auth.can('firewall.manage')"/>
        <v-text-field v-model.number="form.tlsHandshakeSeconds" type="number" min="1" max="120" label="TLS 握手超时（秒）" :readonly="!auth.can('firewall.manage')"/>
        <v-text-field v-model.number="form.preAuthenticationSeconds" type="number" min="1" max="3600" label="登录前最长等待（秒）" :readonly="!auth.can('firewall.manage')"/>
      </div>
      <p class="text-body-2 mb-3">共享出口 IP 的用户共用额度。各服务实例独立计数，重启会清空临时记录。</p>
      <div class="mb-4">
        <div class="font-weight-medium mb-2">服务应用状态</div>
        <p v-if="statusUnavailable" class="text-body-2">暂时无法读取应用状态</p>
        <p v-else-if="!processes.length" class="text-body-2">未收到上报</p>
        <div v-for="process in processes" :key="`${process.instanceName}-${process.roles}`" class="text-body-2 mb-1">
          {{ process.instanceName }} · {{ process.roles }}：{{ statusLabels[process.state] }}
          · 记录 {{ process.trackedSources }} 个 IP · 最后上报 {{ new Date(process.lastSeenAt).toLocaleString() }}
        </div>
      </div>
      <div v-if="auth.can('firewall.manage')" class="d-flex ga-2">
        <v-btn variant="outlined" :disabled="saving" @click="restoreDefaults">恢复默认值</v-btn>
        <v-btn variant="outlined" :disabled="!dirty || saving" @click="undo">撤销修改</v-btn>
        <v-btn color="primary" :loading="saving" :disabled="!dirty" @click="save">保存防护设置</v-btn>
      </div>
    </template>
  </section>
</template>
