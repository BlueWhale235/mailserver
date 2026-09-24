<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ApiError, api, jsonBody } from '../../lib/api'
import PageHeader from '../../components/PageHeader.vue'
import PermissionGrantEditor from '../../components/PermissionGrantEditor.vue'
import StatusPill from '../../components/StatusPill.vue'
import { usePermission } from '../../composables/usePermission'

interface ApiKey {
  id: string
  name: string
  prefix: string
  createdBy: string
  createdAt: string
  lastUsedAt?: string
  expiresAt?: string
  revokedAt?: string
}

interface PermissionCatalogItem {
  code: string
  name: string
  description: string
  groupKey: string
  supportsDomainScope: boolean
}

interface PermissionCatalogGroup {
  key: string
  name: string
  permissions: PermissionCatalogItem[]
}

interface GrantInput {
  permissionCode: string
  resourceType: string
  resourceKey: string
}

interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

const { can } = usePermission()

const rows = ref<ApiKey[]>([])
const totalCount = ref(0)
const page = ref(1)
const pageSize = 20
const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))
const search = ref<string | null>('')
const statusFilter = ref<'Any' | 'Active' | 'Expired' | 'Revoked'>('Any')
let loadVersion = 0
const loading = ref(false)
const error = ref('')
const notice = ref('')

const createOpen = ref(false)
const createLoading = ref(false)
const createResourcesLoading = ref(false)
const createError = ref('')
const createForm = ref({ name: '', expiresAt: null as string | null })
const grantCatalog = ref<PermissionCatalogGroup[]>([])
const grantSelection = ref<GrantInput[]>([{ permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }])
const createBaseline = ref('')

const secretOpen = ref(false)
const secretValue = ref('')
const secretSource = ref('新建')
const copyState = ref<'idle' | 'success' | 'error'>('idle')
const secretAcknowledged = ref(false)

const editGrantsOpen = ref(false)
const editTarget = ref<ApiKey | null>(null)
const editGrants = ref<GrantInput[]>([])
const editGrantsLoading = ref(false)
const editGrantsSaving = ref(false)
const editGrantsError = ref('')
const editGrantsBaseline = ref('')

const rotateTarget = ref<ApiKey | null>(null)
const rotateConfirmation = ref('')
const rotateLoading = ref(false)

const deleteTarget = ref<ApiKey | null>(null)
const deleteConfirmation = ref('')
const deleteLoading = ref(false)
const discardTarget = ref<'create' | 'editGrants' | null>(null)

const createValid = computed(() => createForm.value.name.trim().length > 0 && grantSelection.value.length > 0)
const rotateValid = computed(() => rotateConfirmation.value === rotateTarget.value?.name)
const deleteValid = computed(() => deleteConfirmation.value === deleteTarget.value?.name)
const secretCanClose = computed(() => copyState.value === 'success' || secretAcknowledged.value)
const createSnapshot = computed(() => JSON.stringify({ form: createForm.value, grants: grantSelection.value }))
const createDirty = computed(() => Boolean(createBaseline.value) && createSnapshot.value !== createBaseline.value)
const editGrantsSnapshot = computed(() => JSON.stringify(editGrants.value))
const editGrantsDirty = computed(() => Boolean(editGrantsBaseline.value) && editGrantsSnapshot.value !== editGrantsBaseline.value)
const discardCopy = computed(() => discardTarget.value === 'create'
  ? '新建 API Key 的名称、有效期或初始授权尚未提交。'
  : '当前 API Key 的授权修改尚未保存。')

async function loadKeys() {
  const version = ++loadVersion
  loading.value = true
  error.value = ''
  try {
    const params = new globalThis.URLSearchParams({ page: String(page.value), pageSize: String(pageSize) })
    const normalizedSearch = search.value?.trim() ?? ''
    if (normalizedSearch) params.set('search', normalizedSearch)
    if (statusFilter.value !== 'Any') params.set('status', statusFilter.value)
    const result = await api<PagedResult<ApiKey>>(`/api-keys?${params.toString()}`)
    if (version !== loadVersion) return
    totalCount.value = result.totalCount
    if (page.value > pageCount.value) {
      page.value = pageCount.value
      await loadKeys()
      return
    }
    rows.value = result.items
  } catch (cause) {
    if (version !== loadVersion) return
    rows.value = []
    totalCount.value = 0
    error.value = cause instanceof Error ? cause.message : 'API Key 列表加载失败'
  } finally {
    if (version === loadVersion) loading.value = false
  }
}

function applyFilters() {
  page.value = 1
  void loadKeys()
}

async function loadPermissionResources() {
  grantCatalog.value = await api<PermissionCatalogGroup[]>('/permissions/catalog')
}

async function openCreate() {
  createOpen.value = true
  createError.value = ''
  createForm.value = { name: '', expiresAt: null }
  grantSelection.value = [{ permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }]
  createBaseline.value = createSnapshot.value
  createResourcesLoading.value = true
  try {
    await loadPermissionResources()
  } catch (cause) {
    createError.value = cause instanceof Error ? cause.message : '权限目录加载失败'
  } finally {
    createResourcesLoading.value = false
  }
}

async function createKey() {
  if (!createValid.value) return
  createLoading.value = true
  createError.value = ''
  try {
    const created = await api<{ apiKey: string }>('/api-keys', {
      method: 'POST',
      body: jsonBody({
        name: createForm.value.name.trim(),
        grants: grantSelection.value,
        expiresAt: createForm.value.expiresAt || null,
      }),
    })
    createOpen.value = false
    createBaseline.value = ''
    showSecret(created.apiKey, 'API Key 已创建')
    await loadKeys()
  } catch (cause) {
    createError.value = cause instanceof ApiError || cause instanceof Error ? cause.message : '创建 API Key 失败'
  } finally {
    createLoading.value = false
  }
}

function requestRotation(key: ApiKey) {
  rotateTarget.value = key
  rotateConfirmation.value = ''
}

async function confirmRotation() {
  if (!rotateTarget.value || !rotateValid.value) return
  rotateLoading.value = true
  error.value = ''
  try {
    const targetName = rotateTarget.value.name
    const result = await api<{ apiKey: string }>(`/api-keys/${rotateTarget.value.id}/refresh`, { method: 'POST' })
    rotateTarget.value = null
    rotateConfirmation.value = ''
    showSecret(result.apiKey, `API Key ${targetName} 已完成轮换`)
    await loadKeys()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '轮换 API Key 失败'
  } finally {
    rotateLoading.value = false
  }
}

async function openEditGrants(key: ApiKey) {
  editTarget.value = key
  editGrantsOpen.value = true
  editGrantsLoading.value = true
  editGrantsSaving.value = false
  editGrantsError.value = ''
  editGrants.value = []
  editGrantsBaseline.value = ''
  try {
    const [, current] = await Promise.all([
      loadPermissionResources(),
      api<GrantInput[]>(`/api-keys/${key.id}/grants`),
    ])
    editGrants.value = current.map(grant => ({ ...grant }))
    editGrantsBaseline.value = editGrantsSnapshot.value
  } catch (cause) {
    editGrantsError.value = cause instanceof Error ? cause.message : '授权信息加载失败'
  } finally {
    editGrantsLoading.value = false
  }
}

async function saveEditGrants() {
  if (!editTarget.value || !editGrants.value.length || !editGrantsDirty.value) return
  editGrantsSaving.value = true
  editGrantsError.value = ''
  try {
    await api(`/api-keys/${editTarget.value.id}/grants`, {
      method: 'PUT',
      body: jsonBody({ grants: editGrants.value }),
    })
    editGrantsOpen.value = false
    editGrantsBaseline.value = editGrantsSnapshot.value
    notice.value = `API Key ${editTarget.value.name} 的授权已更新`
  } catch (cause) {
    editGrantsError.value = cause instanceof Error ? cause.message : '保存授权失败'
  } finally {
    editGrantsSaving.value = false
  }
}

function requestDelete(key: ApiKey) {
  deleteTarget.value = key
  deleteConfirmation.value = ''
}

async function confirmDelete() {
  if (!deleteTarget.value || !deleteValid.value) return
  deleteLoading.value = true
  error.value = ''
  try {
    const deletedName = deleteTarget.value.name
    await api(`/api-keys/${deleteTarget.value.id}`, { method: 'DELETE' })
    deleteTarget.value = null
    deleteConfirmation.value = ''
    notice.value = `API Key ${deletedName} 已删除`
    await loadKeys()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '删除 API Key 失败'
  } finally {
    deleteLoading.value = false
  }
}

function showSecret(secret: string, source: string) {
  secretValue.value = secret
  secretSource.value = source
  copyState.value = 'idle'
  secretAcknowledged.value = false
  secretOpen.value = true
}

function closeSecret() {
  if (!secretCanClose.value) return
  secretOpen.value = false
  secretValue.value = ''
  secretAcknowledged.value = false
  copyState.value = 'idle'
}

function requestCloseCreate() {
  if (createLoading.value) return
  if (createDirty.value) discardTarget.value = 'create'
  else createOpen.value = false
}

function requestCloseEditGrants() {
  if (editGrantsSaving.value) return
  if (editGrantsDirty.value) discardTarget.value = 'editGrants'
  else editGrantsOpen.value = false
}

function confirmDiscard() {
  if (discardTarget.value === 'create') {
    createOpen.value = false
    createBaseline.value = ''
  } else if (discardTarget.value === 'editGrants') {
    editGrantsOpen.value = false
    editGrantsBaseline.value = ''
  }
  discardTarget.value = null
}

function protectDraftsOnExit(event: BeforeUnloadEvent) {
  const unacknowledgedSecret = secretOpen.value && !secretCanClose.value
  if (!unacknowledgedSecret && !createDirty.value && !editGrantsDirty.value) return
  event.preventDefault()
  event.returnValue = ''
}

async function copySecret() {
  copyState.value = 'idle'
  try {
    if (!globalThis.navigator.clipboard) throw new Error('clipboard unavailable')
    await globalThis.navigator.clipboard.writeText(secretValue.value)
    copyState.value = 'success'
  } catch {
    copyState.value = 'error'
  }
}

onMounted(() => {
  globalThis.addEventListener('beforeunload', protectDraftsOnExit)
  void loadKeys()
})
onBeforeUnmount(() => {
  globalThis.removeEventListener('beforeunload', protectDraftsOnExit)
  secretValue.value = ''
})
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/system">系统</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>API Key</span>
  </div>

  <PageHeader title="API Key" description="为自动化和外部系统签发独立凭据，密钥只在生成时展示一次">
    <v-btn v-if="can('api-key.manage')" color="primary" prepend-icon="mdi-plus" @click="openCreate">新建 API Key</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>

  <v-card flat class="panel table-panel">
    <div class="table-toolbar">
      <div>
        <strong>已签发凭据</strong>
        <span class="table-summary">共 {{ totalCount }} 个，轮换后旧密钥立即失效</span>
      </div>
      <v-spacer />
      <v-btn icon="mdi-refresh" variant="text" aria-label="刷新 API Key 列表" :loading="loading" @click="loadKeys" />
    </div>
    <div class="key-filters">
      <v-text-field v-model="search" label="搜索名称、前缀或创建人" prepend-inner-icon="mdi-magnify" hide-details density="compact" clearable @keyup.enter="applyFilters" @click:clear="applyFilters" />
      <v-select v-model="statusFilter" label="状态" :items="[{ title: '全部状态', value: 'Any' }, { title: '有效', value: 'Active' }, { title: '已过期', value: 'Expired' }, { title: '已吊销', value: 'Revoked' }]" hide-details density="compact" @update:model-value="applyFilters" />
      <v-btn variant="outlined" :disabled="loading" @click="applyFilters">筛选</v-btn>
    </div>
    <v-table>
      <thead><tr><th>名称</th><th>前缀</th><th>状态</th><th>最近使用</th><th>过期时间</th><th></th></tr></thead>
      <tbody>
        <tr v-if="loading"><td colspan="6"><v-progress-linear indeterminate color="primary" /></td></tr>
        <tr v-else-if="error"><td colspan="6" class="text-center py-10 text-medium-emphasis">API Key 列表加载失败，请点击刷新重试</td></tr>
        <tr v-else-if="!rows.length"><td colspan="6" class="text-center py-10 text-medium-emphasis">{{ search?.trim() || statusFilter !== 'Any' ? '没有符合筛选条件的 API Key' : '还没有创建 API Key' }}</td></tr>
        <tr v-for="key in rows" v-else :key="key.id">
          <td><b>{{ key.name }}</b><small>由 {{ key.createdBy }} 创建</small></td>
          <td><code>{{ key.prefix }}</code></td>
          <td>
            <StatusPill v-if="key.revokedAt" label="已吊销" tone="danger" />
            <StatusPill v-else-if="key.expiresAt && new Date(key.expiresAt) < new Date()" label="已过期" tone="warning" />
            <StatusPill v-else label="有效" tone="success" />
          </td>
          <td>{{ key.lastUsedAt ? new Date(key.lastUsedAt).toLocaleString('zh-CN') : '从未使用' }}</td>
          <td>{{ key.expiresAt ? new Date(key.expiresAt).toLocaleDateString('zh-CN') : '永不过期' }}</td>
          <td class="text-no-wrap text-end">
            <v-btn
              v-if="can('api-key.manage')"
              size="small"
              variant="text"
              icon="mdi-autorenew"
              :disabled="Boolean(key.revokedAt)"
              :aria-label="key.revokedAt ? `${key.name} 已吊销，不能轮换` : `轮换 ${key.name}`"
              @click="requestRotation(key)"
            />
            <v-btn
              v-if="can('iam.manage')"
              size="small"
              variant="text"
              icon="mdi-shield-key-outline"
              :disabled="Boolean(key.revokedAt)"
              :aria-label="key.revokedAt ? `${key.name} 已吊销，不能编辑授权` : `编辑 ${key.name} 的授权`"
              @click="openEditGrants(key)"
            />
            <v-btn v-if="can('api-key.manage')" size="small" variant="text" icon="mdi-delete-outline" color="error" :aria-label="`删除 ${key.name}`" @click="requestDelete(key)" />
          </td>
        </tr>
      </tbody>
    </v-table>
    <div v-if="!error" class="table-footer">
      <span>第 {{ page }} / {{ pageCount }} 页 · 共 {{ totalCount }} 个</span>
      <v-pagination v-model="page" :length="pageCount" density="compact" :disabled="loading" @update:model-value="loadKeys" />
    </div>
  </v-card>

  <v-dialog :model-value="createOpen" max-width="920" persistent>
    <v-card class="grant-dialog-card">
      <v-card-title class="d-flex align-center gap-2"><v-icon color="primary">mdi-key-plus</v-icon> 新建 API Key</v-card-title>
      <v-card-text class="grant-dialog-body">
        <v-alert v-if="createError" type="error" variant="tonal" density="compact" class="mb-3">{{ createError }}</v-alert>
        <div class="form-grid">
          <v-text-field v-model="createForm.name" label="名称" placeholder="例如 metrics-exporter" autofocus />
          <v-text-field v-model="createForm.expiresAt" label="过期时间（可选）" type="datetime-local" />
        </div>
        <v-alert type="info" variant="tonal" density="compact" class="mb-5">建议每个系统使用独立密钥，并只授予完成任务所需的最小权限。</v-alert>
        <div class="section-label">初始授权</div>
        <v-progress-linear v-if="createResourcesLoading" indeterminate color="primary" />
          <PermissionGrantEditor v-else v-model="grantSelection" :catalog="grantCatalog" :disabled="createLoading" />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="createLoading" @click="requestCloseCreate">取消</v-btn><v-btn color="primary" :loading="createLoading" :disabled="!createValid || createResourcesLoading" @click="createKey">创建 API Key</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog v-model="secretOpen" max-width="620" persistent>
    <v-card>
      <v-card-title class="d-flex align-center gap-2"><v-icon color="success">mdi-key-variant</v-icon> API Key 已生成</v-card-title>
      <v-card-text>
        <v-alert type="warning" variant="tonal" class="mb-4">{{ secretSource }}。请立即复制并妥善保存，关闭后无法再次查看完整密钥。</v-alert>
        <v-textarea :model-value="secretValue" label="完整密钥" aria-label="生成的 API Key" readonly rows="3" spellcheck="false" />
        <div class="copy-row">
          <v-btn variant="outlined" :prepend-icon="copyState === 'success' ? 'mdi-check' : 'mdi-content-copy'" @click="copySecret">{{ copyState === 'success' ? '已复制' : '复制密钥' }}</v-btn>
          <span v-if="copyState === 'success'" class="copy-success">密钥已写入剪贴板</span>
          <span v-else-if="copyState === 'error'" class="copy-error">无法访问剪贴板，请手动复制上方密钥</span>
        </div>
        <v-checkbox v-model="secretAcknowledged" label="我已将完整密钥保存到安全位置" hide-details class="mt-3" />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn color="primary" :disabled="!secretCanClose" @click="closeSecret">我已安全保存并关闭</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="editGrantsOpen" max-width="920" persistent>
    <v-card class="grant-dialog-card">
      <v-card-title class="d-flex align-center gap-2"><v-icon color="primary">mdi-shield-key-outline</v-icon> 编辑授权 · {{ editTarget?.name }}</v-card-title>
      <v-card-text class="grant-dialog-body">
        <v-alert v-if="editGrantsError" type="error" variant="tonal" density="compact" class="mb-3">{{ editGrantsError }}</v-alert>
        <v-progress-linear v-if="editGrantsLoading" indeterminate color="primary" />
          <PermissionGrantEditor v-else v-model="editGrants" :catalog="grantCatalog" :disabled="editGrantsSaving" />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="editGrantsSaving" @click="requestCloseEditGrants">取消</v-btn><v-btn color="primary" :loading="editGrantsSaving" :disabled="editGrantsLoading || !editGrants.length || !editGrantsDirty" @click="saveEditGrants">保存授权</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="Boolean(rotateTarget)" max-width="520" persistent @update:model-value="value => { if (!value) rotateTarget = null }">
    <v-card>
      <v-card-title class="d-flex align-center gap-2"><v-icon color="warning">mdi-autorenew</v-icon> 轮换 API Key</v-card-title>
      <v-card-text>
        <v-alert type="warning" variant="tonal" class="mb-4">轮换后旧密钥立即失效。请先确认依赖该密钥的客户端可以同步更新。</v-alert>
        <p class="confirmation-copy">请输入 <strong>{{ rotateTarget?.name }}</strong> 以确认轮换。</p>
        <v-text-field v-model="rotateConfirmation" label="API Key 名称" :disabled="rotateLoading" autofocus />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="rotateLoading" @click="rotateTarget = null">取消</v-btn><v-btn color="warning" :loading="rotateLoading" :disabled="!rotateValid" @click="confirmRotation">确认轮换</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="Boolean(deleteTarget)" max-width="520" persistent @update:model-value="value => { if (!value) deleteTarget = null }">
    <v-card>
      <v-card-title class="d-flex align-center gap-2"><v-icon color="error">mdi-key-remove</v-icon> 删除 API Key</v-card-title>
      <v-card-text>
        <v-alert type="error" variant="tonal" class="mb-4">删除后使用该密钥的客户端将立即失去访问权限，此操作无法撤销。</v-alert>
        <p class="confirmation-copy">请输入 <strong>{{ deleteTarget?.name }}</strong> 以确认删除。</p>
        <v-text-field v-model="deleteConfirmation" label="API Key 名称" :disabled="deleteLoading" autofocus />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="deleteLoading" @click="deleteTarget = null">取消</v-btn><v-btn color="error" :loading="deleteLoading" :disabled="!deleteValid" @click="confirmDelete">确认删除</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="Boolean(discardTarget)" max-width="500" persistent>
    <v-card>
      <v-card-title class="d-flex align-center gap-2"><v-icon color="warning">mdi-alert-outline</v-icon> 放弃未保存修改？</v-card-title>
      <v-card-text>{{ discardCopy }}关闭后这些修改将无法恢复。</v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" @click="discardTarget = null">继续编辑</v-btn><v-btn color="error" variant="tonal" @click="confirmDiscard">放弃修改</v-btn></v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.table-toolbar > div:first-child { display: grid; gap: 3px; }
.table-toolbar strong { color: var(--text); font-size: 14px; }
.table-summary { color: var(--text-muted); font-size: 12px; }
.key-filters { display: grid; grid-template-columns: minmax(250px, 1fr) 180px auto; align-items: center; gap: 12px; padding: 0 20px 16px; }
.form-grid { display: grid; grid-template-columns: minmax(0, 1fr) minmax(260px, .7fr); gap: 16px; }
.section-label { margin: 0 0 10px; color: var(--text-secondary); font-size: 13px; font-weight: 700; }
.grant-dialog-card { max-height: min(880px, 92vh); }
.grant-dialog-body { overflow-y: auto; }
.copy-row { display: flex; align-items: center; gap: 12px; min-height: 40px; }
.copy-success { color: #087a55; font-size: 13px; }
.copy-error { color: #b42318; font-size: 13px; }
.confirmation-copy { margin: 0 0 14px; color: var(--text-secondary); font-size: 14px; }
@media (max-width: 720px) {
  .key-filters { grid-template-columns: 1fr; }
  .form-grid { grid-template-columns: 1fr; gap: 0; }
  .copy-row { align-items: flex-start; flex-direction: column; }
}
</style>
