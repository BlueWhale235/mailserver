<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, jsonBody } from '../lib/api'
import { storageSize, type DirectoryAccount, type DirectoryContext } from '../lib/accountDirectory'
import PageHeader from '../components/PageHeader.vue'
import DirectoryAliases from '../components/DirectoryAliases.vue'
import { usePageDraftGuard } from '../composables/usePageDraftGuard'
const route = useRoute(), router = useRouter()
const domainId = computed(() => String(route.params.id)), accountId = computed(() => String(route.params.accountId ?? ''))
const isNew = computed(() => route.name === 'account-create')
const context = ref<DirectoryContext | null>(null), account = ref<DirectoryAccount | null>(null)
const loading = ref(false), saving = ref(''), error = ref(''), notice = ref(''), deleteOpen = ref(false)
const localPart = ref(''), password = ref(''), confirm = ref('')
const draft = ref({ isEnabled: true, quotaGiB: 10 }), baseline = ref(JSON.stringify(draft.value))
const aliasDirty = ref(false), aliasBusy = ref(false), discardOpen = ref(false)
const settingsDirty = computed(() => JSON.stringify(draft.value) !== baseline.value)
const dirty = computed(() => settingsDirty.value || !!password.value || !!confirm.value || (isNew.value && !!localPart.value) || aliasDirty.value)
const busy = computed(() => !!saving.value || aliasBusy.value)
const { leaveOpen, decideLeave } = usePageDraftGuard(dirty, busy)
const settingsValid = computed(() => Number.isFinite(draft.value.quotaGiB) && draft.value.quotaGiB > 0 && draft.value.quotaGiB * 1024 ** 3 >= (account.value?.usedBytes ?? 0))
const passwordValid = computed(() => password.value.length >= 12 && password.value === confirm.value)
const createValid = computed(() => /^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+$/.test(localPart.value.trim()) && settingsValid.value && passwordValid.value)
const back = computed(() => ({ name: 'account-directory', params: { id: domainId.value }, query: route.query }))
let request = 0
async function load() {
  const current = ++request
  loading.value = true; error.value = ''; context.value = null; account.value = null
  localPart.value = ''; password.value = ''; confirm.value = ''; aliasDirty.value = false
  draft.value = { isEnabled: true, quotaGiB: 10 }; baseline.value = JSON.stringify(draft.value)
  try {
    const next = await api<DirectoryContext>(`/domains/${domainId.value}/account-directory`)
    const row = isNew.value ? null : await api<DirectoryAccount>(`/domains/${domainId.value}/account-directory/accounts/${accountId.value}`)
    if (current !== request) return
    context.value = next; account.value = row
    if (row && next.canReadAccounts) resetSettings()
  } catch (cause) { if (current === request) error.value = (cause as Error).message }
  finally { if (current === request) loading.value = false }
}
function resetSettings() {
  draft.value = { isEnabled: account.value?.isEnabled ?? true, quotaGiB: (account.value?.quotaBytes ?? 10 * 1024 ** 3) / 1024 ** 3 }
  baseline.value = JSON.stringify(draft.value)
}
async function refreshSummary() {
  try { account.value = await api<DirectoryAccount>(`/domains/${domainId.value}/account-directory/accounts/${accountId.value}`) }
  catch (cause) { error.value = (cause as Error).message }
}
watch(() => [route.params.id, route.params.accountId, route.name], () => { void load() }, { immediate: true })
onBeforeUnmount(() => { request++; password.value = ''; confirm.value = '' })
async function create() {
  if (!createValid.value || busy.value || !context.value?.canManageAccounts) return
  saving.value = 'create'; error.value = ''
  try {
    const row = await api<DirectoryAccount>(`/domains/${domainId.value}/accounts`, { method: 'POST', body: jsonBody({ localPart: localPart.value.trim(), password: password.value, quotaBytes: Math.round(draft.value.quotaGiB * 1024 ** 3) }) })
    password.value = ''; confirm.value = ''; localPart.value = ''; baseline.value = JSON.stringify(draft.value); saving.value = ''
    await router.replace({ name: 'account-settings', params: { id: domainId.value, accountId: row.id }, query: route.query })
    notice.value = '账号已创建，可以继续添加别名。'
  } catch (cause) { error.value = (cause as Error).message }
  finally { saving.value = '' }
}
async function saveSettings() {
  if (!settingsValid.value || !settingsDirty.value || busy.value || !context.value?.canManageAccounts) return
  saving.value = 'settings'; error.value = ''; notice.value = ''
  try {
    await api(`/accounts/${accountId.value}`, { method: 'PATCH', body: jsonBody({ isEnabled: draft.value.isEnabled, quotaBytes: Math.round(draft.value.quotaGiB * 1024 ** 3) }) })
    baseline.value = JSON.stringify(draft.value); notice.value = '账号设置已保存。'; await refreshSummary(); aliasRefresh.value++
  } catch (cause) { error.value = (cause as Error).message }
  finally { saving.value = '' }
}
const aliasRefresh = ref(0)
async function savePassword() {
  if (!passwordValid.value || busy.value || !context.value?.canManageAccounts) return
  saving.value = 'password'; error.value = ''; notice.value = ''
  try { await api(`/accounts/${accountId.value}/password`, { method: 'POST', body: jsonBody({ password: password.value }) }); password.value = ''; confirm.value = ''; notice.value = '密码已更新。' }
  catch (cause) { error.value = (cause as Error).message }
  finally { saving.value = '' }
}
async function remove() {
  if (busy.value || account.value?.isEnabled !== false || !context.value?.canManageAccounts) return
  saving.value = 'delete'; error.value = ''
  try {
    await api(`/accounts/${accountId.value}`, { method: 'DELETE' })
    password.value = ''; confirm.value = ''; aliasDirty.value = false; baseline.value = JSON.stringify(draft.value); deleteOpen.value = false; saving.value = ''
    await router.replace(back.value)
  } catch (cause) { error.value = (cause as Error).message }
  finally { saving.value = '' }
}
</script>
<template>
  <PageHeader :title="isNew ? '新建账号' : account?.address || '账号配置'" :description="context ? `${context.domainName} · ${isNew ? '创建后可继续配置别名' : '账号设置与关联别名'}` : '正在加载…'"><v-btn variant="text" :disabled="busy" :to="back">返回账号和别名</v-btn></PageHeader>
  <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}<v-btn v-if="!context" variant="text" @click="load">重试</v-btn></v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" class="mb-4">{{ notice }}</v-alert>
  <v-progress-linear v-if="loading" indeterminate color="primary" />
  <template v-else-if="context && (isNew || account)">
    <v-card v-if="isNew && context.canManageAccounts" flat class="panel settings-panel">
      <h2>账号信息</h2><div class="settings-form"><v-text-field v-model="localPart" label="邮箱本地部分" :suffix="`@${context.domainName}`" :disabled="busy" /><v-text-field v-model.number="draft.quotaGiB" label="容量配额（GiB）" type="number" min="0" step="any" :disabled="busy" /><v-text-field v-model="password" label="初始密码（至少 12 个字符）" type="password" autocomplete="new-password" :disabled="busy" /><v-text-field v-model="confirm" label="再次输入密码" type="password" autocomplete="new-password" :disabled="busy" /></div><div class="settings-actions"><v-btn variant="text" :disabled="busy" :to="back">取消</v-btn><v-btn color="primary" :loading="saving === 'create'" :disabled="!createValid || busy" @click="create">创建账号</v-btn></div>
    </v-card>
    <v-alert v-else-if="isNew" type="warning">没有创建账号的权限。</v-alert>
    <template v-else>
      <div v-if="context.canReadAccounts" class="account-summary"><v-chip :color="account?.isEnabled ? 'success' : 'default'">{{ account?.isEnabled ? '已启用' : '已停用' }}</v-chip><span>已用容量 {{ storageSize(account?.usedBytes ?? null) }} / {{ storageSize(account?.quotaBytes ?? null) }}</span><span v-if="context.canReadAliases">{{ account?.aliasCount }} 个关联别名</span></div>
      <div v-if="context.canReadAccounts" class="settings-grid">
        <v-card flat class="panel settings-panel"><h2>账号设置</h2><p>邮箱地址固定不变，可通过别名增加收发地址。</p><v-text-field :model-value="account?.address" label="邮箱地址" readonly /><v-switch v-model="draft.isEnabled" label="启用账号" color="primary" :disabled="!context.canManageAccounts || busy || aliasDirty" /><v-alert v-if="!draft.isEnabled" type="warning" variant="tonal" class="mb-4">账号停用后无法登录，其关联别名也将不可用。</v-alert><v-text-field v-model.number="draft.quotaGiB" label="容量配额（GiB）" type="number" min="0" step="any" :disabled="!context.canManageAccounts || busy" :error-messages="!settingsValid ? '配额必须大于零，且不能小于已用容量。' : undefined" /><div v-if="context.canManageAccounts" class="settings-actions"><v-btn variant="text" :disabled="!settingsDirty || busy" @click="discardOpen = true">撤销修改</v-btn><v-btn color="primary" :disabled="!settingsValid || !settingsDirty || busy || aliasDirty" :loading="saving === 'settings'" @click="saveSettings">保存账号设置</v-btn></div></v-card>
        <v-card v-if="context.canManageAccounts" flat class="panel settings-panel"><h2>密码设置</h2><p>密码单独保存，不会更改账号或别名地址。</p><v-text-field v-model="password" label="新密码（至少 12 个字符）" type="password" autocomplete="new-password" :disabled="busy" /><v-text-field v-model="confirm" label="再次输入新密码" type="password" autocomplete="new-password" :disabled="busy" /><div class="settings-actions"><v-btn color="primary" :disabled="!passwordValid || busy" :loading="saving === 'password'" @click="savePassword">更新密码</v-btn></div></v-card>
      </div>
      <DirectoryAliases v-if="context.canReadAliases" :key="`${accountId}-${aliasRefresh}`" :domain-id="domainId" :account-id="accountId" :account-address="account?.address" :can-manage="context.canManageAliases && !saving" @dirty="aliasDirty = $event" @busy="aliasBusy = $event" @changed="refreshSummary" />
      <v-card v-if="context.canManageAccounts" flat class="panel account-danger"><div><h3>软删除账号</h3><p>请先停用账号。删除后进入回收站，关联别名可能需要重新绑定。</p></div><v-btn color="error" variant="tonal" :disabled="account?.isEnabled !== false || busy || dirty" @click="error = ''; deleteOpen = true">软删除账号</v-btn></v-card>
    </template>
  </template>
  <v-dialog v-model="deleteOpen" max-width="500" :persistent="busy"><v-card title="确认软删除账号"><v-card-text>{{ account?.address }} 将进入回收站。<v-alert v-if="error" type="error">{{ error }}</v-alert></v-card-text><v-card-actions class="justify-end"><v-btn :disabled="busy" @click="deleteOpen = false">取消</v-btn><v-btn color="error" :disabled="busy" :loading="saving === 'delete'" @click="remove">确认软删除</v-btn></v-card-actions></v-card></v-dialog>
  <v-dialog v-model="discardOpen" max-width="440"><v-card title="撤销账号设置修改？"><v-card-text>账号设置将恢复为最近读取的内容。</v-card-text><v-card-actions class="justify-end"><v-btn @click="discardOpen = false">继续编辑</v-btn><v-btn color="error" @click="resetSettings(); discardOpen = false">确认撤销</v-btn></v-card-actions></v-card></v-dialog>
  <v-dialog v-model="leaveOpen" max-width="440" @update:model-value="value => { if (!value) decideLeave(false) }"><v-card title="放弃未保存的修改？"><v-card-text>离开后当前草稿将丢失。</v-card-text><v-card-actions class="justify-end"><v-btn @click="decideLeave(false)">继续编辑</v-btn><v-btn color="error" @click="decideLeave(true)">放弃修改并离开</v-btn></v-card-actions></v-card></v-dialog>
</template>
<style scoped>
.settings-panel :deep(.v-input){margin-bottom:16px}
.settings-panel{padding:24px}.settings-panel h2{margin-bottom:12px}.settings-panel p,.account-danger p{color:#64748b;margin:0 0 20px}.settings-grid{display:grid;grid-template-columns:1.25fr 1fr;gap:24px;margin:20px 0 24px;align-items:start}.settings-actions{display:flex;justify-content:flex-end;gap:12px}.settings-form{max-width:720px;margin-top:24px}.account-summary{display:flex;align-items:center;gap:24px;padding:16px 0}.account-danger{display:flex;justify-content:space-between;align-items:center;gap:24px;padding:24px;margin-top:24px}.account-danger p{margin:8px 0 0}
</style>
