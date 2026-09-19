<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { inspectFirewallLines } from '../lib/firewallInput'
import PageHeader from '../components/PageHeader.vue'
import { api, jsonBody } from '../lib/api'
import { useAuthStore } from '../stores/auth'
interface Rule { id: string; cidr: string; note?: string; isEnabled: boolean }
const auth = useAuthStore()
const canReadQuarantine = computed(() => auth.permissions.fullAccess
  || auth.permissions.allDomainPermissions.some(code => ['domain.read', 'domain.manage', 'domain.quarantine.read', 'domain.quarantine.manage'].includes(code))
  || Object.entries(auth.permissions.domainPermissions).some(([code, ids]) => ids.length > 0 && ['domain.read', 'domain.manage', 'domain.quarantine.read', 'domain.quarantine.manage'].includes(code)))
const rules = ref<Rule[]>([])
const search = ref<string | null>('')
const statusFilter = ref('all')
const busy = ref(false)
const saving = ref(false)
const error = ref('')
const formError = ref('')
const notice = ref('')
const editor = ref(false)
const target = ref<Rule | null>(null)
const removing = ref<Rule | null>(null)
const confirmation = ref('')
const form = ref({ cidr: '', note: '', isEnabled: true })
const initialForm = ref('')
const riskAccepted = ref(false)
const discardOpen = ref(false)
const dirty = computed(() => editor.value && JSON.stringify(form.value) !== initialForm.value)
const inputCheck = computed(() => inspectFirewallLines(form.value.cidr))
const requiresRiskConfirmation = computed(() => form.value.isEnabled && inputCheck.value.blocksAll && (!target.value || !target.value.isEnabled))
const shown = computed(() => rules.value.filter(rule =>
  (rule.cidr + ' ' + (rule.note || '')).toLowerCase().includes((search.value ?? '').trim().toLowerCase())
  && (statusFilter.value === 'all' || rule.isEnabled === (statusFilter.value === 'enabled')))
  .sort((a, b) => Number(b.isEnabled) - Number(a.isEnabled) || a.cidr.localeCompare(b.cidr)))
async function load() {
  if (busy.value || saving.value || !auth.can('firewall.read')) return
  busy.value = true; error.value = ''
  try { rules.value = await api<Rule[]>('/firewall/rules') }
  catch (e) { error.value = e instanceof Error ? e.message : '防火墙规则加载失败' }
  finally { busy.value = false }
}
function edit(rule: Rule | null) {
  if (!auth.can('firewall.manage') || saving.value) return
  target.value = rule; form.value = rule ? { cidr: rule.cidr, note: rule.note || '', isEnabled: rule.isEnabled } : { cidr: '', note: '', isEnabled: true }
  initialForm.value = JSON.stringify(form.value)
  riskAccepted.value = false
  formError.value = ''; editor.value = true
}
async function save() {
  if (saving.value || !auth.can('firewall.manage')) return
  if (inputCheck.value.errors.length) { formError.value = inputCheck.value.errors.join('；'); return }
  if (requiresRiskConfirmation.value && !riskAccepted.value) { formError.value = '请确认拦截全部地址的风险'; return }
  saving.value = true; formError.value = ''
  try {
    if (target.value) {
      const updated = await api<Rule>(`/firewall/rules/${target.value.id}`, {
        method: 'PUT', body: jsonBody({ isEnabled: form.value.isEnabled, note: form.value.note.trim() }),
      })
      rules.value = [...rules.value.filter(rule => rule.id !== updated.id), updated]
      notice.value = '防火墙规则已保存'; editor.value = false
    } else {
      const entries = [...inputCheck.value.entries]
      const failed: string[] = []
      const failures: string[] = []
      // Sequential requests avoid flooding the API and retain per-line outcomes.
      for (const entry of entries) {
        try {
          const created = await api<Rule>('/firewall/rules', { method: 'POST', body: jsonBody({ cidr: entry.value, note: form.value.note.trim() }) })
          rules.value = [...rules.value.filter(rule => rule.id !== created.id), created]
        } catch (cause) {
          failed.push(entry.value)
          failures.push(`${entry.value}：${cause instanceof Error ? cause.message : '保存失败'}`)
        }
      }
      const succeeded = entries.length - failed.length
      notice.value = succeeded ? `已保存 ${succeeded} 条防火墙规则` : ''
      if (failed.length) {
        form.value.cidr = failed.join('\n')
        formError.value = `成功 ${succeeded} 条，失败 ${failed.length} 条。输入框仅保留未成功项：${failures.join('；')}`
      } else editor.value = false
    }
  } catch (e) { formError.value = e instanceof Error ? e.message : '保存失败' }
  finally { saving.value = false }
}
function requestDelete(rule: Rule) { if (!auth.can('firewall.manage') || saving.value) return; removing.value = rule; confirmation.value = ''; formError.value = '' }
function cancelEdit() { if (saving.value) return; if (dirty.value) discardOpen.value = true; else editor.value = false }
function beforeUnload(event: globalThis.BeforeUnloadEvent) { if (dirty.value || saving.value) { event.preventDefault(); event.returnValue = '' } }
onBeforeRouteLeave(() => !saving.value && (!dirty.value || globalThis.confirm('防火墙规则尚未保存，确定离开？')))
onMounted(() => globalThis.addEventListener('beforeunload', beforeUnload))
onBeforeUnmount(() => globalThis.removeEventListener('beforeunload', beforeUnload))
async function remove() {
  if (!removing.value || saving.value || confirmation.value !== removing.value.cidr || !auth.can('firewall.manage')) return
  saving.value = true; formError.value = ''
  try {
    await api(`/firewall/rules/${removing.value.id}`, { method: 'DELETE' })
    rules.value = rules.value.filter(rule => rule.id !== removing.value?.id)
    removing.value = null; notice.value = '规则已删除'
  } catch (e) { formError.value = e instanceof Error ? e.message : '删除失败' }
  finally { saving.value = false }
}
onMounted(() => { if (auth.can('firewall.read')) void load() })
</script>

<template>
  <PageHeader title="邮件安全" description="管理 IP 防火墙规则与安全配置">
    <v-btn v-if="canReadQuarantine" to="/security/quarantine" variant="outlined" prepend-icon="mdi-email-lock-outline">隔离区</v-btn>
    <v-btn v-if="auth.can('firewall.read')" :loading="busy" :disabled="saving" @click="load">刷新</v-btn>
    <v-btn v-if="auth.can('firewall.manage')" color="primary" prepend-icon="mdi-plus" @click="edit(null)">新建防火墙规则</v-btn>
  </PageHeader>
  <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>
  <section v-if="auth.can('firewall.read')" class="panel">
    <div class="panel-head"><div><h2>IP 防火墙</h2><p>启用的规则将拦截匹配来源的连接，请核对网段范围。</p></div><span>{{ rules.length }} 条规则</span></div>
    <div class="table-toolbar"><v-text-field v-model="search" label="筛选 IP 或备注" clearable hide-details/><v-select v-model="statusFilter" label="规则状态" :items="[{ title: '全部状态', value: 'all' }, { title: '已启用', value: 'enabled' }, { title: '已禁用', value: 'disabled' }]" hide-details/><span class="text-caption">匹配 {{ shown.length }} 条</span></div>
    <v-progress-linear v-if="busy" indeterminate/>
    <v-table><thead><tr><th>IP / CIDR</th><th>备注</th><th>状态</th><th>操作</th></tr></thead><tbody>
      <tr v-for="rule in shown" :key="rule.id"><td>{{ rule.cidr }}</td><td>{{ rule.note || '—' }}</td><td>{{ rule.isEnabled ? '已启用' : '已禁用' }}</td><td><template v-if="auth.can('firewall.manage')"><v-btn variant="text" @click="edit(rule)">编辑</v-btn><v-btn variant="text" color="error" @click="requestDelete(rule)">删除</v-btn></template></td></tr>
      <tr v-if="!shown.length && !busy"><td colspan="4">{{ error ? '规则加载失败，请刷新重试' : search ? '没有匹配的规则' : '尚未配置防火墙规则' }}</td></tr>
    </tbody></v-table>
  </section>
  <v-alert v-else type="info" variant="tonal">当前账号没有查看防火墙的权限。</v-alert>
  <div class="d-flex ga-3 mt-5">
    <v-btn v-if="auth.can('settings.read')" to="/system/plugins" variant="outlined">插件配置</v-btn>
    <v-btn v-if="auth.can('audit.read')" to="/system/audit" variant="outlined">审计日志</v-btn>
    <v-btn v-if="auth.can('settings.read')" to="/system/settings" variant="outlined">全局安全设置</v-btn>
  </div>
  <v-dialog v-model="editor" max-width="640" persistent scrollable><v-card :title="target ? '编辑防火墙规则' : '新建防火墙规则'"><v-form @submit.prevent="save"><v-card-text>
    <v-alert v-if="formError" type="error" variant="tonal" class="mb-4">{{ formError }}</v-alert>
    <v-text-field v-model="form.note" label="备注" placeholder="例如：异常连接来源" :readonly="saving" class="mb-6"/>
    <v-textarea v-model="form.cidr" label="IP / CIDR（一行一个）" :placeholder="'192.0.2.10\n198.51.100.0/24\n2001:db8::/64'" rows="7" :hide-details="false" :error-messages="form.cidr ? inputCheck.errors : []" :readonly="!!target || saving" class="mb-4"/>
    <p class="text-caption mb-4">{{ target ? '已有规则的地址不可修改；需要新增地址请创建新规则。' : `已识别 ${inputCheck.entries.length} 条地址；空行忽略，最多 100 条，共用上方备注。` }}不填写前缀时仅拦截单个地址。</p>
    <v-alert v-if="inputCheck.blocksAll" type="warning" variant="tonal" class="mb-4">/0 会匹配该 IP 协议族的所有来源，可能中断正常邮件连接。</v-alert>
    <v-checkbox v-if="requiresRiskConfirmation" v-model="riskAccepted" label="我确认拦截此协议族的全部来源" :disabled="saving"/>
    <v-switch v-if="target" v-model="form.isEnabled" label="启用拦截" :disabled="saving"/>
    <p class="mt-4 text-body-2">保存后立即影响匹配来源的连接。</p>
  </v-card-text><v-card-actions><v-btn :disabled="saving" @click="cancelEdit">取消</v-btn><v-btn color="primary" type="submit" :loading="saving" :disabled="!!inputCheck.errors.length || (!!target && !dirty) || (requiresRiskConfirmation && !riskAccepted)">{{ target ? '确认保存' : `保存 ${inputCheck.entries.length} 条规则` }}</v-btn></v-card-actions></v-form></v-card></v-dialog>
  <v-dialog v-model="discardOpen" max-width="440" persistent><v-card title="放弃未保存的修改？"><v-card-text>当前规则尚未保存，放弃后无法恢复本次输入。</v-card-text><v-card-actions><v-btn @click="discardOpen = false">继续编辑</v-btn><v-btn color="error" @click="discardOpen = false; editor = false">放弃修改</v-btn></v-card-actions></v-card></v-dialog>
  <v-dialog :model-value="!!removing" max-width="520" persistent><v-card v-if="removing" title="删除防火墙规则"><v-card-text>
    <v-alert v-if="formError" type="error" variant="tonal" class="mb-4">{{ formError }}</v-alert>
    <p class="mb-4">删除后不再通过此规则拦截来源。请输入 {{ removing.cidr }} 确认。</p><v-text-field v-model="confirmation" label="确认 IP / CIDR" :disabled="saving"/>
  </v-card-text><v-card-actions><v-btn :disabled="saving" @click="removing = null">取消</v-btn><v-btn color="error" :loading="saving" :disabled="confirmation !== removing.cidr" @click="remove">确认删除</v-btn></v-card-actions></v-card></v-dialog>
</template>
