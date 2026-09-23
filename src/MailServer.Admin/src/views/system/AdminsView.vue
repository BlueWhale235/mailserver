<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { ApiError, api, jsonBody } from '../../lib/api'
import PageHeader from '../../components/PageHeader.vue'
import PermissionGrantEditor from '../../components/PermissionGrantEditor.vue'
import StatusPill from '../../components/StatusPill.vue'
import { usePermission } from '../../composables/usePermission'

interface AdminUser {
  id: string
  userName: string
  isEnabled: boolean
  createdAt: string
  isPrimary: boolean
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

interface DomainOption {
  id: string
  name: string
}

interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

const { can } = usePermission()

const rows = ref<AdminUser[]>([])
const totalCount = ref(0)
const page = ref(1)
const pageSize = 20
const loading = ref(false)
const error = ref('')
const notice = ref('')
const search = ref<string | null>('')
const statusFilter = ref<'Any' | 'Enabled' | 'Disabled'>('Any')
const saveLoading = ref(false)

const createOpen = ref(false)
const createLoading = ref(false)
const createError = ref('')
const createForm = ref({ userName: '', password: '', isEnabled: true })
const createGrants = ref<GrantInput[]>([{ permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }])
const createBaseline = ref('')
const permissionDomains = ref<DomainOption[]>([])
const permissionLoading = ref(false)

const editOpen = ref(false)
const editTarget = ref<AdminUser | null>(null)
const editForm = ref({ userName: '', password: '', isEnabled: true })
const editBaseline = ref('')

const grantsOpen = ref(false)
const grantSubject = ref<AdminUser | null>(null)
const grantCatalog = ref<PermissionCatalogGroup[]>([])
const grantSelection = ref<GrantInput[]>([])
const grantLoading = ref(false)
const grantSaving = ref(false)
const grantBaseline = ref('')

const deleteTarget = ref<AdminUser | null>(null)
const deleteConfirmation = ref('')
const deleteLoading = ref(false)
const discardTarget = ref<'create' | 'edit' | 'grants' | 'route' | null>(null)
let resolvePendingLeave: ((allow: boolean) => void) | null = null

const createValid = computed(() =>
  createForm.value.userName.trim().length > 0
  && createForm.value.password.length >= 12
  && createGrants.value.length > 0)

const editValid = computed(() =>
  editForm.value.userName.trim().length > 0
  && (!editForm.value.password || editForm.value.password.length >= 12))
const createSnapshot = computed(() => JSON.stringify({ form: createForm.value, grants: createGrants.value }))
const createDirty = computed(() => Boolean(createBaseline.value) && createSnapshot.value !== createBaseline.value)
const editSnapshot = computed(() => JSON.stringify(editForm.value))
const editDirty = computed(() => Boolean(editBaseline.value) && editSnapshot.value !== editBaseline.value)
const grantSnapshot = computed(() => JSON.stringify(grantSelection.value))
const grantDirty = computed(() => Boolean(grantBaseline.value) && grantSnapshot.value !== grantBaseline.value)
const hasDirtyDialog = computed(() => createDirty.value || editDirty.value || grantDirty.value)
const discardCopy = computed(() => {
  if (discardTarget.value === 'create') return '新管理员的账号、密码或初始授权尚未提交。'
  if (discardTarget.value === 'edit') return '管理员资料或新密码尚未保存。'
  if (discardTarget.value === 'grants') return '当前管理员的授权修改尚未保存。'
  const count = [createDirty.value, editDirty.value, grantDirty.value].filter(Boolean).length
  return `当前有 ${count} 个管理员表单尚未保存。`
})

async function loadPermissionResources() {
  const [catalog, domains] = await Promise.all([
    api<PermissionCatalogGroup[]>('/permissions/catalog'),
    api<PagedResult<DomainOption>>('/domains/search?page=1&pageSize=100'),
  ])
  grantCatalog.value = catalog
  permissionDomains.value = domains.items
}

async function loadAdmins() {
  loading.value = true
  error.value = ''
  try {
    const params = new globalThis.URLSearchParams({ page: '1', pageSize: '100' })
    const normalizedSearch = search.value?.trim() ?? ''
    if (normalizedSearch) params.set('search', normalizedSearch)
    if (statusFilter.value !== 'Any') params.set('status', statusFilter.value.toLowerCase())
    params.set('page', String(page.value))
    params.set('pageSize', String(pageSize))
    const result = await api<PagedResult<AdminUser>>(`/admins?${params.toString()}`)
    rows.value = result.items
    totalCount.value = result.totalCount
  } catch (cause) {
    rows.value = []
    totalCount.value = 0
    error.value = cause instanceof Error ? cause.message : '管理员列表加载失败'
  } finally {
    loading.value = false
  }
}

function applyFilters() {
  page.value = 1
  void loadAdmins()
}

async function openCreate() {
  createOpen.value = true
  createError.value = ''
  createForm.value = { userName: '', password: '', isEnabled: true }
  createGrants.value = [{ permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }]
  createBaseline.value = createSnapshot.value
  permissionLoading.value = true
  try {
    await loadPermissionResources()
  } catch (cause) {
    createError.value = cause instanceof Error ? cause.message : '权限目录加载失败'
  } finally {
    permissionLoading.value = false
  }
}

async function createAdmin() {
  if (!createValid.value) return
  createLoading.value = true
  createError.value = ''
  try {
    await api('/admins', {
      method: 'POST',
      body: jsonBody({
        userName: createForm.value.userName.trim(),
        password: createForm.value.password,
        isEnabled: createForm.value.isEnabled,
        grants: createGrants.value,
      }),
    })
    createOpen.value = false
    createBaseline.value = ''
    createForm.value = { userName: '', password: '', isEnabled: true }
    notice.value = '管理员已创建'
    await loadAdmins()
  } catch (cause) {
    createError.value = cause instanceof ApiError ? cause.message : '创建管理员失败'
  } finally {
    createLoading.value = false
  }
}

function openEdit(admin: AdminUser) {
  editTarget.value = admin
  editForm.value = { userName: admin.userName, password: '', isEnabled: admin.isEnabled }
  editBaseline.value = editSnapshot.value
  editOpen.value = true
}

async function saveEdit() {
  if (!editTarget.value || !editValid.value || !editDirty.value) return
  saveLoading.value = true
  error.value = ''
  try {
    await api(`/admins/${editTarget.value.id}`, {
      method: 'PATCH',
      body: jsonBody({
        userName: editForm.value.userName.trim(),
        isEnabled: editForm.value.isEnabled,
        password: editForm.value.password || null,
      }),
    })
    editOpen.value = false
    editForm.value.password = ''
    editBaseline.value = ''
    notice.value = '管理员信息已更新'
    await loadAdmins()
  } catch (cause) {
    error.value = cause instanceof ApiError ? cause.message : '更新管理员失败'
  } finally {
    saveLoading.value = false
  }
}

async function openGrants(admin: AdminUser) {
  grantSubject.value = admin
  grantsOpen.value = true
  grantLoading.value = true
  grantSaving.value = false
  grantSelection.value = []
  grantBaseline.value = ''
  try {
    const [, current] = await Promise.all([
      loadPermissionResources(),
      api<GrantInput[]>(`/admins/${admin.id}/grants`),
    ])
    grantSelection.value = current.map(g => ({ permissionCode: g.permissionCode, resourceType: g.resourceType, resourceKey: g.resourceKey }))
    grantBaseline.value = grantSnapshot.value
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '授权信息加载失败'
  } finally {
    grantLoading.value = false
  }
}

async function saveGrants() {
  if (!grantSubject.value || !grantDirty.value) return
  grantSaving.value = true
  error.value = ''
  try {
    await api(`/admins/${grantSubject.value.id}/grants`, {
      method: 'PUT',
      body: jsonBody({ grants: grantSelection.value }),
    })
    grantsOpen.value = false
    grantBaseline.value = grantSnapshot.value
    notice.value = `已保存 ${grantSubject.value.userName} 的授权`
  } catch (cause) {
    error.value = cause instanceof ApiError ? cause.message : '保存授权失败'
  } finally {
    grantSaving.value = false
  }
}

function openDelete(admin: AdminUser) {
  deleteTarget.value = admin
  deleteConfirmation.value = ''
  error.value = ''
}

async function confirmDelete() {
  if (!deleteTarget.value || deleteConfirmation.value !== deleteTarget.value.userName) return
  deleteLoading.value = true
  error.value = ''
  try {
    const userName = deleteTarget.value.userName
    await api(`/admins/${deleteTarget.value.id}`, { method: 'DELETE' })
    deleteTarget.value = null
    notice.value = `管理员 ${userName} 已删除`
    await loadAdmins()
  } catch (cause) {
    error.value = cause instanceof ApiError ? cause.message : '删除管理员失败'
  } finally {
    deleteLoading.value = false
  }
}

function requestClose(target: 'create' | 'edit' | 'grants') {
  const saving = target === 'create' ? createLoading.value : target === 'edit' ? saveLoading.value : grantSaving.value
  if (saving) return
  const dirty = target === 'create' ? createDirty.value : target === 'edit' ? editDirty.value : grantDirty.value
  if (dirty) {
    discardTarget.value = target
    return
  }
  if (target === 'create') createOpen.value = false
  else if (target === 'edit') editOpen.value = false
  else grantsOpen.value = false
}

function cancelDiscard() {
  if (discardTarget.value === 'route') resolvePendingLeave?.(false)
  resolvePendingLeave = null
  discardTarget.value = null
}

function confirmDiscard() {
  const target = discardTarget.value
  discardTarget.value = null
  if (target === 'route') {
    const resolve = resolvePendingLeave
    resolvePendingLeave = null
    resolve?.(true)
    return
  }
  if (target === 'create') {
    createOpen.value = false
    createForm.value = { userName: '', password: '', isEnabled: true }
    createGrants.value = [{ permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }]
    createBaseline.value = ''
  } else if (target === 'edit') {
    editOpen.value = false
    editForm.value.password = ''
    editBaseline.value = ''
  } else if (target === 'grants') {
    grantsOpen.value = false
    grantSelection.value = []
    grantBaseline.value = ''
  }
}

function protectPageExit(event: BeforeUnloadEvent) {
  if (!hasDirtyDialog.value && !createLoading.value && !saveLoading.value && !grantSaving.value) return
  event.preventDefault()
  event.returnValue = ''
}

onBeforeRouteLeave(() => {
  if (createLoading.value || saveLoading.value || grantSaving.value) {
    error.value = '管理员信息正在保存，请等待操作完成后再离开。'
    return false
  }
  if (!hasDirtyDialog.value) return true
  if (resolvePendingLeave) resolvePendingLeave(false)
  discardTarget.value = 'route'
  return new Promise<boolean>(resolve => { resolvePendingLeave = resolve })
})
onMounted(() => {
  globalThis.addEventListener('beforeunload', protectPageExit)
  void loadAdmins()
})
onBeforeUnmount(() => {
  globalThis.removeEventListener('beforeunload', protectPageExit)
  resolvePendingLeave?.(false)
  resolvePendingLeave = null
  createForm.value.password = ''
  editForm.value.password = ''
})
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/system">系统</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>管理员与权限</span>
  </div>

  <PageHeader title="管理员与权限" description="管理后台管理员、RBAC 授权与账号启停">
    <v-btn v-if="can('admin.account.manage')" color="primary" prepend-icon="mdi-plus" @click="openCreate">新建管理员</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>

  <v-card flat class="panel table-panel">
    <div class="table-toolbar">
      <v-text-field v-model="search" prepend-inner-icon="mdi-magnify" placeholder="搜索管理员账号" hide-details density="compact" clearable @keyup.enter="applyFilters" @click:clear="applyFilters" />
      <v-select v-model="statusFilter" :items="[{ title: '全部状态', value: 'Any' }, { title: '已启用', value: 'Enabled' }, { title: '已停用', value: 'Disabled' }]" hide-details density="compact" max-width="150" @update:model-value="applyFilters" />
      <v-spacer />
      <v-btn icon="mdi-refresh" variant="text" aria-label="刷新管理员列表" :loading="loading" @click="loadAdmins" />
    </div>
    <v-table>
      <thead><tr><th>用户</th><th>状态</th><th>角色</th><th>创建时间</th><th></th></tr></thead>
      <tbody>
        <tr v-if="loading"><td colspan="5"><v-progress-linear indeterminate color="primary" /></td></tr>
        <tr v-else-if="!rows.length"><td colspan="5" class="text-center py-10 text-medium-emphasis">没有符合条件的系统管理员</td></tr>
        <tr v-for="admin in rows" v-else :key="admin.id">
          <td><b>{{ admin.userName }}</b><small>ID {{ admin.id.slice(0, 8) }}…</small></td>
          <td><StatusPill :label="admin.isEnabled ? '已启用' : '已停用'" :tone="admin.isEnabled ? 'success' : 'neutral'" /></td>
          <td>{{ admin.isPrimary ? '主管理员' : '管理员' }}</td>
          <td>{{ new Date(admin.createdAt).toLocaleDateString('zh-CN') }}</td>
          <td class="text-no-wrap text-end">
            <v-btn v-if="can('iam.manage')" size="small" variant="text" prepend-icon="mdi-shield-key-outline"
              :disabled="admin.isPrimary" :aria-label="admin.isPrimary ? '主管理员权限不可修改' : `编辑 ${admin.userName} 的授权`"
              @click="openGrants(admin)">授权</v-btn>
            <v-btn v-if="can('admin.account.manage')" size="small" variant="text" icon="mdi-pencil-outline" :aria-label="`编辑 ${admin.userName}`" @click="openEdit(admin)" />
            <v-btn v-if="can('admin.account.manage') && !admin.isPrimary" size="small" variant="text" color="error"
              icon="mdi-delete-outline" :aria-label="`删除 ${admin.userName}`" @click="openDelete(admin)" />
          </td>
        </tr>
      </tbody>
    </v-table>
    <div class="table-footer">
      <span>共 {{ totalCount }} 个管理员</span>
      <v-pagination v-model="page" :length="Math.max(1, Math.ceil(totalCount / pageSize))" density="compact" @update:model-value="loadAdmins" />
    </div>
  </v-card>

  <v-dialog :model-value="createOpen" max-width="880" persistent>
    <v-card>
      <v-card-title class="d-flex align-center gap-2"><v-icon color="primary">mdi-account-plus-outline</v-icon> 新建管理员</v-card-title>
      <v-card-text>
        <v-alert v-if="createError" type="error" variant="tonal" density="compact" class="mb-3">{{ createError }}</v-alert>
        <v-text-field v-model="createForm.userName" label="用户名" placeholder="operator" autofocus
          :rules="[value => !!String(value).trim() || '请输入用户名']" />
        <v-text-field v-model="createForm.password" label="初始密码" type="password" placeholder="至少 12 位"
          hint="创建后管理员可自行修改" persistent-hint :rules="[value => String(value).length >= 12 || '密码至少需要 12 个字符']" />
        <v-switch v-model="createForm.isEnabled" label="立即启用" color="primary" />
        <div class="section-label">初始授权</div>
        <v-progress-linear v-if="permissionLoading" indeterminate class="mb-4" />
        <PermissionGrantEditor v-else v-model="createGrants" :catalog="grantCatalog" :domains="permissionDomains" :disabled="createLoading" />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="createLoading" @click="requestClose('create')">取消</v-btn><v-btn color="primary" :loading="createLoading" :disabled="!createValid || permissionLoading" @click="createAdmin">创建管理员</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="editOpen" max-width="520" persistent>
    <v-card>
      <v-card-title class="d-flex align-center gap-2"><v-icon color="primary">mdi-account-edit-outline</v-icon> 编辑管理员</v-card-title>
      <v-card-text>
        <v-alert v-if="editTarget?.isPrimary" type="info" variant="tonal" density="compact" class="mb-4">主管理员的用户名和启用状态不可修改，但可以重设密码。</v-alert>
        <v-text-field v-model="editForm.userName" label="用户名" :readonly="editTarget?.isPrimary"
          :rules="[value => !!String(value).trim() || '请输入用户名']" />
        <v-text-field v-model="editForm.password" label="新密码（留空不修改）" type="password" placeholder="至少 12 位"
          :rules="[value => !value || String(value).length >= 12 || '密码至少需要 12 个字符']" />
        <v-switch v-model="editForm.isEnabled" label="启用账号" color="primary" :disabled="editTarget?.isPrimary" />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="saveLoading" @click="requestClose('edit')">取消</v-btn><v-btn color="primary" :loading="saveLoading" :disabled="!editValid || !editDirty" @click="saveEdit">保存修改</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="grantsOpen" max-width="880" persistent>
    <v-card>
      <v-card-title class="d-flex align-center gap-2">
        <v-icon color="primary">mdi-shield-key-outline</v-icon> 编辑授权 · {{ grantSubject?.userName }}
      </v-card-title>
      <v-card-text>
        <v-progress-linear v-if="grantLoading" indeterminate />
        <PermissionGrantEditor v-else v-model="grantSelection" :catalog="grantCatalog" :domains="permissionDomains" :disabled="grantSaving" />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="grantSaving" @click="requestClose('grants')">取消</v-btn><v-btn color="primary" :loading="grantSaving" :disabled="grantLoading || !grantSelection.length || !grantDirty" @click="saveGrants">保存授权</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="Boolean(deleteTarget)" max-width="520" :persistent="deleteLoading"
    @update:model-value="value => { if (!value && !deleteLoading) deleteTarget = null }">
    <v-card v-if="deleteTarget">
      <v-card-title class="d-flex align-center gap-2"><v-icon color="error">mdi-account-remove-outline</v-icon> 删除管理员</v-card-title>
      <v-card-text>
        <v-alert type="warning" variant="tonal" density="compact" class="mb-4">删除后，该管理员的会话和授权将失效，此操作无法撤销。</v-alert>
        <p class="mb-4">请输入管理员名称 <b>{{ deleteTarget.userName }}</b> 以确认。</p>
        <v-text-field v-model="deleteConfirmation" label="管理员名称" :disabled="deleteLoading" autofocus />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="deleteLoading" @click="deleteTarget = null">取消</v-btn><v-btn color="error" :loading="deleteLoading" :disabled="deleteConfirmation !== deleteTarget.userName" @click="confirmDelete">确认删除</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="Boolean(discardTarget)" max-width="500" persistent>
    <v-card>
      <v-card-title class="d-flex align-center gap-2"><v-icon color="warning">mdi-alert-outline</v-icon> 放弃未保存修改？</v-card-title>
      <v-card-text>{{ discardCopy }}关闭或离开后，这些修改将无法恢复。</v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" @click="cancelDiscard">继续编辑</v-btn><v-btn color="error" variant="tonal" @click="confirmDiscard">放弃修改</v-btn></v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.section-label { margin: 8px 0 12px; color: var(--text-secondary); font-size: 13px; font-weight: 700; }
</style>
