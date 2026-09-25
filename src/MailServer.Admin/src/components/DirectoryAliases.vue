<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, jsonBody } from '../lib/api'
import type { DirectoryAlias, DirectoryPage } from '../lib/accountDirectory'
const props = defineProps<{ domainId: string; accountId?: string; accountAddress?: string; pending?: boolean; canManage: boolean }>()
const emit = defineEmits<{ changed: []; dirty: [value: boolean]; busy: [value: boolean] }>()
const rows = ref<DirectoryAlias[]>([]), page = ref(1), total = ref(0), search = ref('')
const loading = ref(false), busy = ref(false), error = ref(''), notice = ref('')
const editor = ref(false), editId = ref<string | null>(null), baseline = ref(''), discardOpen = ref(false)
const draft = ref({ sourceLocalPart: '', targetAccountId: '', mode: 'SendReceive' as DirectoryAlias['mode'] })
const targets = ref<{ id: string; address: string }[]>([]), targetSearch = ref(''), targetPage = ref(1), targetTotal = ref(0), targetLoading = ref(false)
const deleteTarget = ref<DirectoryAlias | null>(null)
const dirty = computed(() => editor.value && JSON.stringify(draft.value) !== baseline.value)
const valid = computed(() => /^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+$/.test(draft.value.sourceLocalPart.trim()) && !!draft.value.targetAccountId)
const pages = computed(() => Math.max(1, Math.ceil(total.value / 20)))
watch(dirty, value => emit('dirty', value), { immediate: true })
watch(busy, value => emit('busy', value))
let request = 0, targetRequest = 0
async function load() {
  const current = ++request
  loading.value = true; error.value = ''
  const query = new URLSearchParams({ page: String(page.value), pageSize: '20', query: search.value ?? '' })
  if (props.accountId) query.set('accountId', props.accountId)
  if (props.pending) query.set('pending', 'true')
  try {
    const result = await api<DirectoryPage<DirectoryAlias>>(`/domains/${props.domainId}/account-directory/aliases?${query}`)
    if (current !== request) return
    rows.value = result.items; total.value = result.totalCount
    if (page.value > pages.value) page.value = pages.value
  } catch (cause) { if (current === request) { rows.value = []; error.value = (cause as Error).message } }
  finally { if (current === request) loading.value = false }
}
watch(() => [props.domainId, props.accountId, props.pending, page.value], () => { void load() }, { immediate: true })
function find() { if (page.value === 1) void load(); else page.value = 1 }
async function loadTargets(append = false) {
  const current = ++targetRequest
  targetLoading.value = true
  try {
    const result = await api<DirectoryPage<{ id: string; address: string }>>(`/domains/${props.domainId}/account-directory/targets?${new URLSearchParams({ query: targetSearch.value ?? '', page: String(targetPage.value) })}`)
    if (current !== targetRequest) return
    const selected = targets.value.filter(x => x.id === draft.value.targetAccountId)
    targets.value = [...new Map([...(append ? targets.value : selected), ...result.items].map(x => [x.id, x])).values()]
    targetTotal.value = result.totalCount
  } catch (cause) { if (current === targetRequest) error.value = (cause as Error).message }
  finally { if (current === targetRequest) targetLoading.value = false }
}
function searchTargets(value: string) { targetSearch.value = value; targetPage.value = 1; void loadTargets() }
function moreTargets() { targetPage.value++; void loadTargets(true) }
function open(alias?: DirectoryAlias) {
  if (editor.value || busy.value) return
  editId.value = alias?.id ?? null
  draft.value = { sourceLocalPart: alias?.source.split('@')[0] ?? '', targetAccountId: alias?.targetAccountId ?? props.accountId ?? '', mode: alias?.mode ?? 'SendReceive' }
  targets.value = draft.value.targetAccountId ? [{ id: draft.value.targetAccountId, address: alias?.targetAddress ?? props.accountAddress ?? '原目标账号（不可用）' }] : []
  baseline.value = JSON.stringify(draft.value); editor.value = true; error.value = ''; notice.value = ''; targetSearch.value = ''; targetPage.value = 1
  void loadTargets()
}
function close() { if (busy.value) return; if (dirty.value) discardOpen.value = true; else editor.value = false }
async function save() {
  if (!valid.value || busy.value || !props.canManage || (editId.value && !dirty.value)) return
  busy.value = true; error.value = ''
  try {
    await api(`/domains/${props.domainId}/aliases${editId.value ? `/${editId.value}` : ''}`, { method: editId.value ? 'PUT' : 'POST', body: jsonBody({ ...draft.value, sourceLocalPart: draft.value.sourceLocalPart.trim() }) })
    editor.value = false; notice.value = '别名已保存。'; await load(); emit('changed')
  } catch (cause) { error.value = (cause as Error).message }
  finally { busy.value = false }
}
async function remove() {
  if (!deleteTarget.value || busy.value || !props.canManage) return
  busy.value = true; error.value = ''
  try { await api(`/aliases/${deleteTarget.value.id}`, { method: 'DELETE' }); deleteTarget.value = null; notice.value = '别名已删除。'; await load(); emit('changed') }
  catch (cause) { error.value = (cause as Error).message }
  finally { busy.value = false }
}
</script>

<template>
  <v-card flat class="panel alias-directory">
    <div class="alias-heading"><div><h2>{{ pending ? '待处理别名' : '关联别名' }}</h2><p>{{ pending ? '这些别名的目标账号不存在或已删除，请重新绑定。' : '别名与当前账号共用收件箱；可分别配置收发权限。' }}</p></div><v-btn v-if="canManage && !pending" color="primary" :disabled="editor || busy" @click="open()">添加别名</v-btn></div>
    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}<v-btn v-if="!editor && !deleteTarget" variant="text" @click="load">重试</v-btn></v-alert>
    <v-alert v-if="notice" type="success" variant="tonal" class="mb-4">{{ notice }}</v-alert>
    <div v-if="editor" class="alias-editor">
      <h3>{{ editId ? '编辑别名' : '添加别名' }}</h3>
      <v-text-field v-model="draft.sourceLocalPart" label="别名本地部分" :disabled="busy" />
      <v-autocomplete v-model="draft.targetAccountId" :items="targets" item-title="address" item-value="id" label="目标账号" :loading="targetLoading" :disabled="busy" no-filter @update:search="searchTargets">
        <template #append-item><v-btn v-if="targetPage * 20 < targetTotal" block variant="text" :disabled="targetLoading" @click="moreTargets">加载更多账号</v-btn></template>
      </v-autocomplete>
      <v-select v-model="draft.mode" label="收发模式" :disabled="busy" :items="[{title:'可收可发',value:'SendReceive'},{title:'仅收信',value:'ReceiveOnly'},{title:'停用',value:'Disabled'}]" />
      <p v-if="editId && accountId && draft.targetAccountId !== accountId">保存后该别名将移至目标账号，并从当前列表移除。</p>
      <div class="alias-actions"><v-btn variant="text" :disabled="busy" @click="close">取消</v-btn><v-btn color="primary" :loading="busy" :disabled="!canManage || !valid || busy || (!!editId && !dirty)" @click="save">保存别名</v-btn></div>
    </div>
    <form class="alias-search" @submit.prevent="find"><v-text-field v-model="search" label="搜索关联别名" hide-details clearable @click:clear="search = ''; find()" /><v-btn type="submit" variant="tonal" :disabled="loading">搜索</v-btn></form>
    <v-table><thead><tr><th>别名地址</th><th v-if="pending">原目标</th><th>模式</th><th>状态</th><th class="text-right">操作</th></tr></thead><tbody>
      <tr v-if="loading"><td :colspan="pending ? 5 : 4">正在加载别名…</td></tr>
      <tr v-else-if="!rows.length && !error"><td :colspan="pending ? 5 : 4">{{ pending ? '没有待处理别名' : '没有符合条件的别名' }}</td></tr>
      <tr v-for="alias in loading ? [] : rows" :key="alias.id"><td>{{ alias.source }}</td><td v-if="pending">{{ alias.targetAddress || '目标账号不可用' }}</td><td>{{ alias.mode === 'SendReceive' ? '可收可发' : alias.mode === 'ReceiveOnly' ? '仅收信' : '停用' }}</td><td>{{ alias.unavailableReason || '正常' }}</td><td class="text-right"><v-btn v-if="canManage" variant="text" :disabled="busy || editor" @click="open(alias)">{{ pending ? '重新绑定' : '编辑' }}</v-btn><v-btn v-if="canManage" color="error" variant="text" :disabled="busy || editor" @click="deleteTarget = alias">删除</v-btn><span v-if="!canManage">只读</span></td></tr>
    </tbody></v-table>
    <div class="alias-footer"><span>共 {{ total }} 个别名</span><v-pagination v-model="page" :length="pages" :total-visible="5" :disabled="loading" /></div>
  </v-card>
  <v-dialog :model-value="!!deleteTarget" max-width="480" :persistent="busy" @update:model-value="value => { if (!value && !busy) deleteTarget = null }"><v-card title="确认删除别名"><v-card-text>{{ deleteTarget?.source }} 将被永久删除，不会删除账号中的邮件。<v-alert v-if="error" type="error">{{ error }}</v-alert></v-card-text><v-card-actions class="justify-end"><v-btn :disabled="busy" @click="deleteTarget = null">取消</v-btn><v-btn color="error" :loading="busy" :disabled="busy" @click="remove">确认删除</v-btn></v-card-actions></v-card></v-dialog>
  <v-dialog v-model="discardOpen" max-width="440"><v-card title="放弃别名修改？"><v-card-text>未保存的内容将丢失。</v-card-text><v-card-actions class="justify-end"><v-btn @click="discardOpen = false">继续编辑</v-btn><v-btn color="error" @click="editor = false; discardOpen = false">放弃修改</v-btn></v-card-actions></v-card></v-dialog>
</template>
<style scoped>
.alias-editor :deep(.v-input){margin-bottom:16px}
.alias-directory{padding:24px}.alias-heading,.alias-footer,.alias-search{display:flex;align-items:center;justify-content:space-between;gap:16px}.alias-heading p{color:#64748b;margin:6px 0 20px}.alias-search{margin:20px 0;max-width:560px}.alias-editor{background:#f8fafc;border:1px solid #dbe3ef;padding:24px;border-radius:12px;max-width:760px;margin:12px 0}.alias-editor h3{margin-bottom:20px}.alias-actions{display:flex;justify-content:flex-end;gap:10px}.alias-footer{margin-top:16px}.alias-directory td{overflow-wrap:anywhere}
</style>
