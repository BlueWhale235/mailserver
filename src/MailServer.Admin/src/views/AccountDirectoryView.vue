<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, jsonBody } from '../lib/api'
import { storageSize, type DirectoryAccount, type DirectoryContext, type DirectoryPage } from '../lib/accountDirectory'
import PageHeader from '../components/PageHeader.vue'
import DirectoryAliases from '../components/DirectoryAliases.vue'
import { usePageDraftGuard } from '../composables/usePageDraftGuard'
defineProps<{ embedded?: boolean }>()
const route = useRoute(), router = useRouter()
const domainId = computed(() => String(route.params.id))
const context = ref<DirectoryContext | null>(null), rows = ref<DirectoryAccount[]>([]), total = ref(0)
const query = ref(String(route.query.query ?? '')), enabled = ref(String(route.query.enabled ?? 'all'))
const page = computed(() => Math.max(1, Number(route.query.page) || 1))
const pending = computed(() => route.query.view === 'pending')
const loading = ref(false), busy = ref(false), error = ref(''), notice = ref(''), selected = ref<string[]>([])
const deletion = ref<DirectoryAccount[]>([]), aliasesDirty = ref(false), aliasesBusy = ref(false)
const { leaveOpen, decideLeave } = usePageDraftGuard(aliasesDirty, computed(() => busy.value || aliasesBusy.value))
const pages = computed(() => Math.max(1, Math.ceil(total.value / 20)))
const allSelected = computed(() => !!rows.value.length && rows.value.every(x => selected.value.includes(x.id)))
let request = 0
async function load() {
  const current = ++request
  loading.value = true; error.value = ''; selected.value = []; context.value = null; rows.value = []
  try {
    const next = await api<DirectoryContext>(`/domains/${domainId.value}/account-directory`)
    if (current !== request) return
    context.value = next
    if (pending.value) return
    const params = new URLSearchParams({ page: String(page.value), pageSize: '20', query: String(route.query.query ?? '') })
    if (route.query.enabled === 'true' || route.query.enabled === 'false') params.set('isEnabled', String(route.query.enabled))
    const result = await api<DirectoryPage<DirectoryAccount>>(`/domains/${domainId.value}/account-directory/search?${params}`)
    if (current !== request) return
    rows.value = result.items; total.value = result.totalCount
    if (page.value > pages.value) void setPage(pages.value)
  } catch (cause) { if (current === request) error.value = (cause as Error).message }
  finally { if (current === request) loading.value = false }
}
watch(() => route.fullPath, () => { query.value = String(route.query.query ?? ''); enabled.value = String(route.query.enabled ?? 'all'); void load() }, { immediate: true })
function filter() { void router.replace({ query: { ...route.query, query: query.value?.trim() || undefined, enabled: enabled.value === 'all' ? undefined : enabled.value, page: undefined } }); if (query.value === String(route.query.query ?? '') && enabled.value === String(route.query.enabled ?? 'all')) void load() }
function setPage(value: number) { return router.replace({ query: { ...route.query, page: value === 1 ? undefined : value } }) }
function toggle(id: string) { selected.value = selected.value.includes(id) ? selected.value.filter(x => x !== id) : [...selected.value, id] }
async function batch(action: string) {
  if (!selected.value.length || busy.value || !context.value?.canManageAccounts) return
  const ids = rows.value.filter(x => selected.value.includes(x.id)).map(x => x.id)
  busy.value = true; error.value = ''; notice.value = ''
  try { await api(`/domains/${domainId.value}/accounts/${action}`, { method: 'POST', body: jsonBody({ ids }) }); notice.value = '账号状态已更新。'; await load() }
  catch (cause) { error.value = (cause as Error).message }
  finally { busy.value = false }
}
async function remove() {
  if (busy.value || !deletion.value.length || deletion.value.some(x => x.isEnabled) || !context.value?.canManageAccounts) return
  busy.value = true; error.value = ''
  try { await api(`/domains/${domainId.value}/accounts/batch-delete`, { method: 'POST', body: jsonBody({ ids: deletion.value.map(x => x.id) }) }); deletion.value = []; notice.value = '账号已软删除，可在回收站恢复。'; await load() }
  catch (cause) { error.value = (cause as Error).message }
  finally { busy.value = false }
}
function detailRoute(row: DirectoryAccount) { return { name: 'account-settings', params: { id: domainId.value, accountId: row.id }, query: { ...route.query } } }
</script>
<template>
  <PageHeader v-if="!embedded" title="账号和别名" :description="context ? `${context.domainName} · 按账号管理邮箱与关联别名` : '加载邮件域…'">
    <v-btn v-if="context?.canReadDomain" variant="text" :to="`/domains/${domainId}`">返回域详情</v-btn>
    <v-btn v-else-if="context" variant="text" to="/account-domains">选择邮件域</v-btn>
    <v-btn v-if="context?.canManageAccounts" color="primary" :to="{ name: 'account-create', params: { id: domainId }, query: route.query }">新建账号</v-btn>
  </PageHeader>
  <div v-else class="directory-intro"><div><h2>账号和别名</h2><p>按账号查看邮箱与关联别名，可同时搜索账号和别名地址。</p></div><v-btn v-if="context?.canManageAccounts" color="primary" prepend-icon="mdi-plus" :to="{ name: 'account-create', params: { id: domainId }, query: route.query }">新建账号</v-btn></div>
  <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}<v-btn variant="text" @click="load">重试</v-btn></v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" class="mb-4">{{ notice }}</v-alert>
  <div class="directory-tabs"><v-btn :variant="pending ? 'text' : 'tonal'" :disabled="aliasesDirty || aliasesBusy" :to="{ query: { ...route.query, view: undefined, page: undefined } }">账号列表</v-btn><v-btn v-if="context?.canReadAliases" :variant="pending ? 'tonal' : 'text'" :disabled="aliasesDirty || aliasesBusy" :to="{ query: { ...route.query, view: 'pending', page: undefined } }">待处理别名</v-btn></div>
  <DirectoryAliases v-if="pending && context?.canReadAliases" :key="domainId" :domain-id="domainId" pending :can-manage="context.canManageAliases" @dirty="aliasesDirty = $event" @busy="aliasesBusy = $event" />
  <v-card v-else flat class="panel directory-panel">
    <form class="directory-filter" @submit.prevent="filter">
      <v-text-field v-model="query" :label="context?.canReadAliases ? '搜索账号或别名地址' : '搜索账号地址'" clearable hide-details prepend-inner-icon="mdi-magnify" @click:clear="query = ''; filter()" />
      <v-select v-if="context?.canReadAccounts" v-model="enabled" label="账号状态" :items="[{title:'全部状态',value:'all'},{title:'已启用',value:'true'},{title:'已停用',value:'false'}]" hide-details />
      <v-btn type="submit" color="primary" :disabled="loading">搜索</v-btn>
    </form>
    <div v-if="context?.canManageAccounts" class="directory-batch"><span>已选 {{ selected.length }} 个账号（仅当前页）</span><v-btn variant="text" :disabled="!selected.length || busy || loading" @click="batch('batch-enable')">批量启用</v-btn><v-btn variant="text" :disabled="!selected.length || busy || loading" @click="batch('batch-disable')">批量停用</v-btn><v-btn color="error" variant="text" :disabled="!selected.length || busy || loading" @click="error = ''; deletion = rows.filter(x => selected.includes(x.id)).map(x => ({ ...x }))">批量软删除</v-btn></div>
    <v-table><thead><tr><th v-if="context?.canManageAccounts"><v-checkbox-btn :model-value="allSelected" aria-label="选择当前页账号" :disabled="busy || loading" @update:model-value="value => selected = value ? rows.map(x => x.id) : []" /></th><th>账号地址</th><th v-if="context?.canReadAccounts">状态</th><th v-if="context?.canReadAccounts">已用容量 / 配额</th><th v-if="context?.canReadAliases">别名数量</th><th class="text-right">操作</th></tr></thead><tbody>
      <tr v-if="loading"><td colspan="6">正在加载账号…</td></tr><tr v-else-if="!rows.length && !error"><td colspan="6" class="directory-empty">没有符合条件的账号</td></tr>
      <tr v-for="row in loading ? [] : rows" :key="row.id"><td v-if="context?.canManageAccounts"><v-checkbox-btn :model-value="selected.includes(row.id)" :aria-label="`选择 ${row.address}`" :disabled="busy" @update:model-value="toggle(row.id)" /></td><td><strong>{{ row.address }}</strong><small v-if="row.matchedAlias" class="matched-alias">匹配别名：{{ row.matchedAlias }}<span v-if="row.matchedAliasCount > 1"> 等 {{ row.matchedAliasCount }} 个</span></small></td><td v-if="context?.canReadAccounts"><v-chip size="small" :color="row.isEnabled ? 'success' : 'default'" variant="tonal">{{ row.isEnabled ? '已启用' : '已停用' }}</v-chip></td><td v-if="context?.canReadAccounts">{{ storageSize(row.usedBytes) }} / {{ storageSize(row.quotaBytes) }}</td><td v-if="context?.canReadAliases">{{ row.aliasCount }}</td><td class="text-right"><v-btn variant="text" color="primary" :to="detailRoute(row)" :aria-label="`查看 ${row.address} 的详情`">详情</v-btn></td></tr>
    </tbody></v-table>
    <div class="directory-footer"><span>共 {{ total }} 个账号 · 每页 20 个</span><v-pagination :model-value="page" :length="pages" :total-visible="5" :disabled="loading || busy" @update:model-value="setPage" /></div>
  </v-card>
  <v-dialog :model-value="!!deletion.length" max-width="520" :persistent="busy" @update:model-value="value => { if (!value && !busy) deletion = [] }"><v-card title="确认软删除账号"><v-card-text><p>账号将进入回收站，关联别名可能变为不可用。</p><p v-for="row in deletion" :key="row.id">{{ row.address }}</p><v-alert v-if="deletion.some(x => x.isEnabled)" type="warning">请先取消并停用所选账号，再执行软删除。</v-alert><v-alert v-if="error" type="error">{{ error }}</v-alert></v-card-text><v-card-actions class="justify-end"><v-btn :disabled="busy" @click="deletion = []">取消</v-btn><v-btn color="error" :loading="busy" :disabled="busy || deletion.some(x => x.isEnabled)" @click="remove">确认软删除</v-btn></v-card-actions></v-card></v-dialog>
  <v-dialog v-model="leaveOpen" max-width="440" @update:model-value="value => { if (!value) decideLeave(false) }"><v-card title="放弃未保存的修改？"><v-card-text>离开后当前草稿将丢失。</v-card-text><v-card-actions class="justify-end"><v-btn @click="decideLeave(false)">继续编辑</v-btn><v-btn color="error" @click="decideLeave(true)">放弃修改并离开</v-btn></v-card-actions></v-card></v-dialog>
</template>
<style scoped>
.directory-panel{padding:24px}.directory-tabs{display:flex;gap:8px;margin-bottom:16px}.directory-filter{display:flex;gap:16px;align-items:center;margin-bottom:20px}.directory-filter .v-text-field{flex:1;max-width:650px}.directory-filter .v-select{max-width:190px}.directory-batch,.directory-footer{display:flex;align-items:center;gap:12px;justify-content:flex-end;margin:12px 0}.directory-batch span,.directory-footer span{margin-right:auto;color:#64748b}.matched-alias{display:block;color:#386299;margin:6px 0;overflow-wrap:anywhere}.directory-empty{padding:48px!important;text-align:center;color:#64748b}td{overflow-wrap:anywhere}
.directory-intro{display:flex;align-items:center;justify-content:space-between;gap:16px;margin:22px 0 16px}.directory-intro h2{margin:0;font-size:19px}.directory-intro p{margin:5px 0 0;color:#64748b;font-size:13px}
</style>
