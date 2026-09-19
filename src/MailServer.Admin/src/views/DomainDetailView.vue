<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import PageHeader from '../components/PageHeader.vue'
import StatusPill from '../components/StatusPill.vue'
import DomainRuleWorkspace from '../components/DomainRuleWorkspace.vue'
import DomainCertificateWorkspace from '../components/DomainCertificateWorkspace.vue'
import DomainDnsWorkspace from '../components/DomainDnsWorkspace.vue'
import QueueWorkspace from './delivery/QueueWorkspace.vue'
import { validatePublishedDns, type DomainDnsCheck } from '../lib/domainDns'
import { usePermission } from '../composables/usePermission'
import { api, jsonBody } from '../lib/api'
import type { DomainCertificateModel } from '../lib/domainCertificate'

type AliasMode = 'SendReceive' | 'ReceiveOnly'

interface DomainModel {
  id: string
  name: string
  mailHostName: string
  smtpHostName?: string
  outboundIpAddresses?: string
  isActive: boolean
  quotaBytes: number
  usedBytes: number
  directDeliverSameDomain: boolean
  dkimSelector?: string
  dkimPublicKey?: string
  createdAt: string
}


interface DomainManagement {
  domain: DomainModel
  certificate: DomainCertificateModel | null
  dnsSettings: {
    spfAllPolicy: string
    dmarcPolicy: string
    dmarcPercentage: number
    mtaStsMode: string
    mtaStsMaxAgeSeconds: number
    tlsReportUris: string
    daneEnabled: boolean
  }
  connections: Array<{ protocol: string; hostName: string; port: number; security: string; authenticationRequired: boolean; description: string }>
  outboundAddressOptions: Array<{ address: string; interfaceName: string; addressFamily: string }>
}

interface DomainHealth {
  accountCount: number
  enabledAccountCount: number
  aliasCount: number
  pendingDeliveries: number
  failedDeliveries: number
  hasCertificate: boolean
  certificateNotAfter?: string
  hasDkim: boolean
  lastAuditAt?: string
  pendingQuarantineCount: number
}

interface Account {
  id: string
  address: string
  isEnabled: boolean
  quotaBytes: number
  usedBytes: number
  createdAt: string
  aliasCount: number
}

interface Alias {
  id: string
  source: string
  targetAccountId: string | null
  targetAddress: string | null
  isEnabled: boolean
  mode: AliasMode
  canReceive: boolean
  canSend: boolean
}




interface QuarantineListItem {
  id: string
  messageId: string
  sender: string
  recipients: string[]
  remoteAddress: string
  pluginId: string | null
  pluginName: string | null
  reason: string
  status: 'PendingReview' | 'Released' | 'Deleted'
  quarantinedAt: string
  reviewedBy: string | null
  reviewedAt: string | null
  reviewNote: string | null
  size: number
  internetMessageId: string | null
}

interface QuarantineDetailView {
  record: QuarantineListItem
  fileName: string
  content: string
}

interface DomainAuditEntry {
  id: string
  actor: string
  action: string
  resource: string
  detail: string | null
  createdAt: string
}


interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

const route = useRoute()
const router = useRouter()
const { can } = usePermission()
const domainId = computed(() => String(route.params.id))

const tabKeys = ['overview', 'accounts', 'aliases', 'rules', 'quarantine', 'queue', 'audit', 'dns', 'cert', 'quota'] as const
type TabKey = typeof tabKeys[number]
function normalizeTab(value: unknown): TabKey {
  const candidate = String(value ?? '')
  return (tabKeys as readonly string[]).includes(candidate) ? candidate as TabKey : 'overview'
}

const tab = ref<TabKey>(normalizeTab(route.params.tab))
const management = ref<DomainManagement | null>(null)
const health = ref<DomainHealth | null>(null)
const dnsCheck = ref<DomainDnsCheck | null>(null)
const accounts = ref<Account[]>([])
const aliases = ref<Alias[]>([])
const accountTotal = ref(0)
const aliasTotal = ref(0)
const loading = ref(false)
const dnsLoading = ref(false)
const accountLoading = ref(false)
const aliasLoading = ref(false)
const saving = ref(false)
const error = ref('')
const notice = ref('')
const accountSearch = ref('')
const accountEnabled = ref<boolean | null>(null)
const aliasSearch = ref('')
let accountTimer: ReturnType<typeof globalThis.setTimeout> | undefined
let aliasTimer: ReturnType<typeof globalThis.setTimeout> | undefined

const domainEditOpen = ref(false)
const domainDraft = ref({ mailHostName: '', isActive: true })
const accountDialogOpen = ref(false)
const accountDraft = ref({ localPart: '', password: '', quotaGiB: 10 })
const aliasDialogOpen = ref(false)
const aliasDraft = ref<{ sourceLocalPart: string; targetAccountId: string | null; mode: AliasMode }>({ sourceLocalPart: '', targetAccountId: null, mode: 'SendReceive' })
const quotaGiB = ref(0)
const dialogError = ref('')

const accountSelected = ref<string[]>([])
const accountBusy = ref(false)
const accountEditOpen = ref(false)
const accountEditTarget = ref<Account | null>(null)
const accountEditDraft = ref({ isEnabled: true, quotaGiB: 10 })
const accountPasswordOpen = ref(false)
const accountPasswordTarget = ref<Account | null>(null)
const accountPasswordDraft = ref({ password: '', confirm: '' })

const aliasSelected = ref<string[]>([])
const aliasBusy = ref(false)
const aliasEditOpen = ref(false)
const aliasEditTarget = ref<Alias | null>(null)
const aliasEditDraft = ref<{ sourceLocalPart: string; targetAccountId: string | null; mode: AliasMode }>({ sourceLocalPart: '', targetAccountId: null, mode: 'SendReceive' })

const quarantineRows = ref<QuarantineListItem[]>([])
const quarantineSearch = ref('')
const quarantineStatus = ref<QuarantineListItem['status'] | null>(null)
const quarantinePage = ref(1)
const quarantinePageSize = 10
const quarantineTotal = ref(0)
const quarantineLoading = ref(false)
const quarantineBusy = ref(false)
const quarantineDetailOpen = ref(false)
const quarantineDetail = ref<QuarantineDetailView | null>(null)
const quarantineDetailLoading = ref(false)
const quarantineActionTarget = ref<QuarantineListItem | null>(null)
const quarantineActionKind = ref<'release' | 'delete' | null>(null)
const quarantineNote = ref('')

const auditRows = ref<DomainAuditEntry[]>([])
const auditLoading = ref(false)
const queueRefreshKey = ref(0)

const domainDeleteOpen = ref(false)
const domainDeleteConfirm = ref('')
const domainDeleteBusy = ref(false)

let quarantineTimer: ReturnType<typeof globalThis.setTimeout> | undefined

const domain = computed(() => management.value?.domain ?? null)
const certificate = computed(() => management.value?.certificate ?? null)
const quotaPercent = computed(() => domain.value?.quotaBytes ? Math.min(100, domain.value.usedBytes / domain.value.quotaBytes * 100) : 0)
const readinessAttention = computed(() => {
  const items = dnsCheck.value?.validation?.items ?? []
  return items.filter(item => item.status.toLowerCase() !== 'match').length + (dnsCheck.value?.validation?.warnings.length ?? 0)
})
const accountOptions = computed(() => accounts.value.filter(account => account.isEnabled).map(account => ({ title: account.address, value: account.id })))
const minimumQuotaGiB = computed(() => Math.max(1, Math.ceil((domain.value?.usedBytes ?? 0) / 1024 ** 3)))
const quotaValid = computed(() => Number.isFinite(quotaGiB.value) && quotaGiB.value >= minimumQuotaGiB.value && quotaGiB.value <= 102_400)
const accountValid = computed(() => /^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+$/i.test(accountDraft.value.localPart.trim()) && accountDraft.value.password.length >= 12 && accountDraft.value.quotaGiB >= 1)
const accountAllSelected = computed(() => accounts.value.length > 0 && accounts.value.every(account => accountSelected.value.includes(account.id)))
const aliasAllSelected = computed(() => aliases.value.length > 0 && aliases.value.every(alias => aliasSelected.value.includes(alias.id)))
const quarantinePageCount = computed(() => Math.max(1, Math.ceil(quarantineTotal.value / quarantinePageSize)))
const accountPasswordValid = computed(() => accountPasswordDraft.value.password.length >= 12 && accountPasswordDraft.value.password === accountPasswordDraft.value.confirm)
const accountEditValid = computed(() => {
  const target = accountEditTarget.value
  if (!target) return false
  const giB = Number(accountEditDraft.value.quotaGiB)
  return Number.isFinite(giB) && giB >= 1 && giB <= 10240 && Math.round(giB * 1024 ** 3) >= target.usedBytes
})
const aliasEditValid = computed(() => /^[a-z0-9.!#$%&'*+/=?^`{|}~-]+$/i.test(aliasEditDraft.value.sourceLocalPart.trim()) && Boolean(aliasEditDraft.value.targetAccountId))
const aliasValid = computed(() => /^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+$/i.test(aliasDraft.value.sourceLocalPart.trim()) && Boolean(aliasDraft.value.targetAccountId))

const accountStatusItems = [
  { title: '全部账号', value: null },
  { title: '已启用', value: true },
  { title: '已停用', value: false },
]
const aliasModeItems = [
  { title: '可收可发', value: 'SendReceive' },
  { title: '仅收信', value: 'ReceiveOnly' },
]
const quarantineStatusItems = [
  { title: '全部状态', value: null },
  { title: '待审核', value: 'PendingReview' },
  { title: '已放行', value: 'Released' },
  { title: '已删除', value: 'Deleted' },
]

async function loadPage() {
  loading.value = true
  error.value = ''
  try {
    const [nextManagement, nextHealth, nextDns] = await Promise.all([
      api<DomainManagement>(`/domains/${domainId.value}`),
      api<DomainHealth>(`/domains/${domainId.value}/health`),
      api<DomainDnsCheck>(`/domains/${domainId.value}/dns/check`),
    ])
    management.value = nextManagement
    health.value = nextHealth
    dnsCheck.value = nextDns
    quotaGiB.value = Math.round(nextManagement.domain.quotaBytes / 1024 ** 3)
    await Promise.all([loadAccounts(), loadAliases()])
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '域详情加载失败'
  } finally {
    loading.value = false
  }
}

async function loadAccounts() {
  accountLoading.value = true
  try {
    const query = new globalThis.URLSearchParams({ page: '1', pageSize: '20' })
    if (accountSearch.value.trim()) query.set('query', accountSearch.value.trim())
    if (accountEnabled.value !== null) query.set('isEnabled', String(accountEnabled.value))
    const result = await api<PagedResult<Account>>(`/domains/${domainId.value}/accounts/search?${query}`)
    accounts.value = result.items
    accountTotal.value = result.totalCount
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '邮箱账号加载失败'
  } finally {
    accountLoading.value = false
  }
}

async function loadAliases() {
  aliasLoading.value = true
  try {
    const query = new globalThis.URLSearchParams({ page: '1', pageSize: '20' })
    if (aliasSearch.value.trim()) query.set('query', aliasSearch.value.trim())
    const result = await api<PagedResult<Alias>>(`/domains/${domainId.value}/aliases/search?${query}`)
    aliases.value = result.items
    aliasTotal.value = result.totalCount
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '邮箱别名加载失败'
  } finally {
    aliasLoading.value = false
  }
}


async function runDnsCheck() {
  dnsLoading.value = true
  error.value = ''
  tab.value = 'dns'
  try {
    dnsCheck.value = await api<DomainDnsCheck>(`/domains/${domainId.value}/dns/check`, { method: 'POST' })
    notice.value = 'Direct MX 检查已完成，结果已更新。'
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Direct MX 检查失败'
  } finally {
    dnsLoading.value = false
  }
}

function openDomainEdit() {
  if (!domain.value) return
  domainDraft.value = { mailHostName: domain.value.mailHostName, isActive: domain.value.isActive }
  dialogError.value = ''
  domainEditOpen.value = true
}

async function saveDomain() {
  if (!domain.value || saving.value) return
  saving.value = true
  dialogError.value = ''
  try {
    await api(`/domains/${domainId.value}/details`, {
      method: 'PUT',
      body: jsonBody({
        mailHostName: domainDraft.value.mailHostName,
        isActive: domainDraft.value.isActive,
        smtpHostName: domain.value.smtpHostName ?? null,
        outboundIpAddresses: domain.value.outboundIpAddresses ?? null,
        directDeliverSameDomain: domain.value.directDeliverSameDomain,
      }),
    })
    domain.value.mailHostName = domainDraft.value.mailHostName.trim().toLowerCase().replace(/\.$/, '')
    domain.value.isActive = domainDraft.value.isActive
    domainEditOpen.value = false
    notice.value = '域基础配置已保存。'
  } catch (cause) {
    dialogError.value = cause instanceof Error ? cause.message : '域配置保存失败'
  } finally {
    saving.value = false
  }
}


function toggleAccountAll(value: boolean | null) {
  accountSelected.value = value ? accounts.value.map(account => account.id) : []
}

function toggleAccount(id: string, value: boolean | null) {
  accountSelected.value = value ? Array.from(new Set([...accountSelected.value, id])) : accountSelected.value.filter(item => item !== id)
}

async function batchAccounts(action: 'batch-enable' | 'batch-disable' | 'batch-delete') {
  if (!accountSelected.value.length || accountBusy.value || saving.value) return
  accountBusy.value = true
  error.value = ''
  notice.value = ''
  try {
    await api(`/domains/${domainId.value}/accounts/${action}`, { method: 'POST', body: jsonBody({ ids: accountSelected.value }) })
    notice.value = action === 'batch-delete' ? `已删除 ${accountSelected.value.length} 个邮箱账号。` : action === 'batch-enable' ? `已启用 ${accountSelected.value.length} 个邮箱账号。` : `已停用 ${accountSelected.value.length} 个邮箱账号。`
    accountSelected.value = []
    await Promise.all([loadAccounts(), reloadHealth()])
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '批量操作失败'
  } finally {
    accountBusy.value = false
  }
}

async function toggleAccountEnabled(account: Account) {
  if (accountBusy.value || saving.value) return
  accountBusy.value = true
  error.value = ''
  notice.value = ''
  try {
    await api(`/accounts/${account.id}`, { method: 'PATCH', body: jsonBody({ isEnabled: !account.isEnabled }) })
    account.isEnabled = !account.isEnabled
    notice.value = `${account.address} 已${account.isEnabled ? '启用' : '停用'}。`
    await reloadHealth()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '账号状态更新失败'
  } finally {
    accountBusy.value = false
  }
}

function openAccountEdit(account: Account) {
  accountEditTarget.value = account
  accountEditDraft.value = { isEnabled: account.isEnabled, quotaGiB: Math.round(account.quotaBytes / 1024 ** 3) }
  dialogError.value = ''
  accountEditOpen.value = true
}

async function saveAccountEdit() {
  const target = accountEditTarget.value
  if (!target || !accountEditValid.value || accountBusy.value) return
  accountBusy.value = true
  dialogError.value = ''
  try {
    await api(`/accounts/${target.id}`, {
      method: 'PATCH',
      body: jsonBody({ isEnabled: accountEditDraft.value.isEnabled, quotaBytes: Math.round(Number(accountEditDraft.value.quotaGiB) * 1024 ** 3) }),
    })
    accountEditOpen.value = false
    notice.value = `${target.address} 的账号设置已保存。`
    await Promise.all([loadAccounts(), reloadHealth()])
  } catch (cause) {
    dialogError.value = cause instanceof Error ? cause.message : '账号设置保存失败'
  } finally {
    accountBusy.value = false
  }
}

function openAccountPassword(account: Account) {
  accountPasswordTarget.value = account
  accountPasswordDraft.value = { password: '', confirm: '' }
  dialogError.value = ''
  accountPasswordOpen.value = true
}

async function saveAccountPassword() {
  const target = accountPasswordTarget.value
  if (!target || !accountPasswordValid.value || accountBusy.value) return
  accountBusy.value = true
  dialogError.value = ''
  try {
    await api(`/accounts/${target.id}/password`, { method: 'POST', body: jsonBody({ password: accountPasswordDraft.value.password }) })
    accountPasswordOpen.value = false
    notice.value = `${target.address} 的登录密码已重置。`
  } catch (cause) {
    dialogError.value = cause instanceof Error ? cause.message : '密码重置失败'
  } finally {
    accountBusy.value = false
  }
}

async function deleteAccount(account: Account) {
  if (accountBusy.value || saving.value) return
  accountBusy.value = true
  error.value = ''
  notice.value = ''
  try {
    await api(`/accounts/${account.id}`, { method: 'DELETE' })
    notice.value = `${account.address} 已软删除，可在回收站恢复或等待到期清理。`
    await Promise.all([loadAccounts(), reloadHealth()])
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '账号删除失败'
  } finally {
    accountBusy.value = false
  }
}

function toggleAliasAll(value: boolean | null) {
  aliasSelected.value = value ? aliases.value.map(alias => alias.id) : []
}

function toggleAlias(id: string, value: boolean | null) {
  aliasSelected.value = value ? Array.from(new Set([...aliasSelected.value, id])) : aliasSelected.value.filter(item => item !== id)
}

async function batchAliasMode(mode: AliasMode) {
  if (!aliasSelected.value.length || aliasBusy.value || saving.value) return
  aliasBusy.value = true
  error.value = ''
  notice.value = ''
  try {
    await api(`/domains/${domainId.value}/aliases/batch-mode`, { method: 'POST', body: jsonBody({ ids: aliasSelected.value, mode }) })
    notice.value = `已将 ${aliasSelected.value.length} 个别名调整为「${aliasModeLabel(mode)}」。`
    aliasSelected.value = []
    await Promise.all([loadAliases(), reloadHealth()])
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '批量调整别名失败'
  } finally {
    aliasBusy.value = false
  }
}

async function batchAliasDelete() {
  if (!aliasSelected.value.length || aliasBusy.value || saving.value) return
  aliasBusy.value = true
  error.value = ''
  notice.value = ''
  try {
    await api(`/domains/${domainId.value}/aliases/batch-delete`, { method: 'POST', body: jsonBody({ ids: aliasSelected.value }) })
    notice.value = `已删除 ${aliasSelected.value.length} 个邮箱别名。`
    aliasSelected.value = []
    await Promise.all([loadAliases(), reloadHealth()])
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '批量删除别名失败'
  } finally {
    aliasBusy.value = false
  }
}

function openAliasEdit(alias: Alias) {
  aliasEditTarget.value = alias
  aliasEditDraft.value = {
    sourceLocalPart: alias.source.split('@')[0] ?? '',
    targetAccountId: alias.targetAccountId ?? accountOptions.value[0]?.value ?? null,
    mode: alias.mode === 'ReceiveOnly' ? 'ReceiveOnly' : 'SendReceive',
  }
  dialogError.value = ''
  aliasEditOpen.value = true
}

async function saveAliasEdit() {
  const target = aliasEditTarget.value
  if (!target || !aliasEditValid.value || aliasBusy.value) return
  aliasBusy.value = true
  dialogError.value = ''
  try {
    await api(`/domains/${domainId.value}/aliases/${target.id}`, {
      method: 'PUT',
      body: jsonBody({ sourceLocalPart: aliasEditDraft.value.sourceLocalPart.trim(), targetAccountId: aliasEditDraft.value.targetAccountId, mode: aliasEditDraft.value.mode }),
    })
    aliasEditOpen.value = false
    notice.value = '邮箱别名已更新。'
    await Promise.all([loadAliases(), reloadHealth()])
  } catch (cause) {
    dialogError.value = cause instanceof Error ? cause.message : '别名更新失败'
  } finally {
    aliasBusy.value = false
  }
}

async function deleteAlias(alias: Alias) {
  if (aliasBusy.value || saving.value) return
  aliasBusy.value = true
  error.value = ''
  notice.value = ''
  try {
    await api(`/aliases/${alias.id}`, { method: 'DELETE' })
    notice.value = `${alias.source} 已删除。`
    await Promise.all([loadAliases(), reloadHealth()])
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '别名删除失败'
  } finally {
    aliasBusy.value = false
  }
}


async function loadQuarantine() {
  quarantineLoading.value = true
  error.value = ''
  try {
    const query = new globalThis.URLSearchParams({ page: String(quarantinePage.value), pageSize: String(quarantinePageSize) })
    if (quarantineSearch.value.trim()) query.set('search', quarantineSearch.value.trim())
    if (quarantineStatus.value) query.set('status', quarantineStatus.value)
    const result = await api<PagedResult<QuarantineListItem>>(`/domains/${domainId.value}/quarantine?${query}`)
    quarantineRows.value = result.items
    quarantineTotal.value = result.totalCount
    if (quarantinePage.value > quarantinePageCount.value) quarantinePage.value = quarantinePageCount.value
  } catch (cause) {
    quarantineRows.value = []
    quarantineTotal.value = 0
    error.value = cause instanceof Error ? cause.message : '隔离邮件加载失败'
  } finally {
    quarantineLoading.value = false
  }
}

async function openQuarantineDetail(item: QuarantineListItem) {
  quarantineDetailOpen.value = true
  quarantineDetailLoading.value = true
  quarantineDetail.value = null
  error.value = ''
  try {
    quarantineDetail.value = await api<QuarantineDetailView>(`/domains/${domainId.value}/quarantine/${item.id}/raw`)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '隔离邮件原文加载失败'
    quarantineDetailOpen.value = false
  } finally {
    quarantineDetailLoading.value = false
  }
}

function requestQuarantineAction(item: QuarantineListItem, kind: 'release' | 'delete') {
  quarantineActionTarget.value = item
  quarantineActionKind.value = kind
  quarantineNote.value = ''
}

async function confirmQuarantineAction() {
  const target = quarantineActionTarget.value
  const kind = quarantineActionKind.value
  if (!target || !kind || quarantineBusy.value) return
  quarantineBusy.value = true
  error.value = ''
  notice.value = ''
  try {
    if (kind === 'release') await api(`/domains/${domainId.value}/quarantine/${target.id}/release`, { method: 'POST', body: jsonBody({ note: quarantineNote.value }) })
    else await api(`/domains/${domainId.value}/quarantine/${target.id}`, { method: 'DELETE', body: jsonBody({ note: quarantineNote.value }) })
    quarantineActionTarget.value = null
    quarantineActionKind.value = null
    notice.value = kind === 'release' ? '隔离邮件已放行并重新进入投递流程。' : '隔离邮件已删除。'
    await Promise.all([loadQuarantine(), reloadHealth()])
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '隔离操作失败'
  } finally {
    quarantineBusy.value = false
  }
}

async function loadAudit() {
  auditLoading.value = true
  error.value = ''
  try {
    auditRows.value = await api<DomainAuditEntry[]>(`/domains/${domainId.value}/audit`)
  } catch (cause) {
    auditRows.value = []
    error.value = cause instanceof Error ? cause.message : '域审计加载失败'
  } finally {
    auditLoading.value = false
  }
}

function openDomainDelete() {
  domainDeleteConfirm.value = ''
  dialogError.value = ''
  domainDeleteOpen.value = true
}

async function deleteDomain() {
  if (!domain.value || domainDeleteConfirm.value !== domain.value.name || domainDeleteBusy.value) return
  domainDeleteBusy.value = true
  dialogError.value = ''
  try {
    await api(`/domains/${domainId.value}`, { method: 'DELETE' })
    await router.push({ name: 'domains' })
  } catch (cause) {
    dialogError.value = cause instanceof Error ? cause.message : '邮件域删除失败'
  } finally {
    domainDeleteBusy.value = false
  }
}

async function toggleDomainActive() {
  if (!domain.value || saving.value) return
  saving.value = true
  error.value = ''
  try {
    await api(`/domains/${domainId.value}`, { method: 'PATCH', body: jsonBody({ isActive: !domain.value.isActive }) })
    domain.value.isActive = !domain.value.isActive
    notice.value = domain.value.isActive ? '邮件域已启用。' : '邮件域已停用，不再接受新会话。'
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '域状态更新失败'
  } finally {
    saving.value = false
  }
}

function openAccountDialog() {
  accountDraft.value = { localPart: '', password: '', quotaGiB: 10 }
  dialogError.value = ''
  accountDialogOpen.value = true
}

async function createAccount() {
  if (!accountValid.value || saving.value) return
  saving.value = true
  dialogError.value = ''
  try {
    await api(`/domains/${domainId.value}/accounts`, {
      method: 'POST',
      body: jsonBody({ localPart: accountDraft.value.localPart.trim(), password: accountDraft.value.password, quotaBytes: Math.round(accountDraft.value.quotaGiB * 1024 ** 3) }),
    })
    accountDialogOpen.value = false
    notice.value = `邮箱 ${accountDraft.value.localPart.trim()}@${domain.value?.name} 已创建。`
    await Promise.all([loadAccounts(), reloadHealth()])
  } catch (cause) {
    dialogError.value = cause instanceof Error ? cause.message : '邮箱创建失败'
  } finally {
    saving.value = false
  }
}

function openAliasDialog() {
  aliasDraft.value = { sourceLocalPart: '', targetAccountId: accountOptions.value[0]?.value ?? null, mode: 'SendReceive' }
  dialogError.value = ''
  aliasDialogOpen.value = true
}

async function createAlias() {
  if (!aliasValid.value || saving.value) return
  saving.value = true
  dialogError.value = ''
  try {
    await api(`/domains/${domainId.value}/aliases`, { method: 'POST', body: jsonBody(aliasDraft.value) })
    aliasDialogOpen.value = false
    notice.value = `别名 ${aliasDraft.value.sourceLocalPart.trim()}@${domain.value?.name} 已创建。`
    await Promise.all([loadAliases(), reloadHealth()])
  } catch (cause) {
    dialogError.value = cause instanceof Error ? cause.message : '别名创建失败'
  } finally {
    saving.value = false
  }
}

async function saveQuota() {
  if (!quotaValid.value || saving.value || !domain.value) return
  saving.value = true
  error.value = ''
  try {
    const quotaBytes = Math.round(quotaGiB.value * 1024 ** 3)
    await api(`/domains/${domainId.value}/quota`, { method: 'PUT', body: jsonBody({ quotaBytes }) })
    domain.value.quotaBytes = quotaBytes
    notice.value = `域存储配额已更新为 ${quotaGiB.value} GiB。`
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '域配额保存失败'
  } finally {
    saving.value = false
  }
}

async function validateDns() {
  if (!domain.value) return
  dnsLoading.value = true
  error.value = ''
  try {
    const validation = await validatePublishedDns(domainId.value)
    if (dnsCheck.value) dnsCheck.value.validation = validation
    const attention = validation.items.filter(item => item.status.toLowerCase() !== 'match').length
    notice.value = attention ? `DNS 发布校验完成，有 ${attention} 项需要处理。` : 'DNS 发布校验通过，所有记录均已匹配。'
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'DNS 发布校验失败'
  } finally {
    dnsLoading.value = false
  }
}

function onDnsSaved(config: { settings: DomainManagement['dnsSettings'] }) {
  if (management.value) management.value.dnsSettings = { ...management.value.dnsSettings, ...config.settings }
  notice.value = 'DNS 安全策略已保存。'
}

function onDkimGenerated(payload: { selector: string; publicKey: string }) {
  if (management.value) {
    management.value.domain.dkimSelector = payload.selector
    management.value.domain.dkimPublicKey = payload.publicKey
  }
  if (health.value) health.value.hasDkim = true
  notice.value = `DKIM 密钥已生成，selector：${payload.selector}。`
}

async function reloadHealth() {
  health.value = await api<DomainHealth>(`/domains/${domainId.value}/health`)
}

async function onCertificateChanged(next: DomainCertificateModel | null) {
  if (management.value) management.value.certificate = next
  notice.value = next ? 'TLS 证书已更新并立即生效。' : 'TLS 证书已删除。'
  try { await reloadHealth() } catch { /* summary refresh is best effort */ }
}

function bytes(value?: number) {
  if (!value) return '0 B'
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB']
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1)
  const scaled = value / 1024 ** index
  return `${scaled.toFixed(scaled >= 10 || index === 0 ? 0 : 1)} ${units[index]}`
}

function date(value?: string) {
  return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
}

function dnsStatusView(status?: DomainDnsCheck['status']) {
  if (status === 'Healthy') return { label: readinessAttention.value ? '已就绪，有提示' : '已就绪', tone: readinessAttention.value ? 'warning' as const : 'success' as const }
  if (status === 'Unhealthy') return { label: '需要处理', tone: 'danger' as const }
  if (status === 'Checking') return { label: '检查中', tone: 'neutral' as const }
  return { label: '尚未检查', tone: 'neutral' as const }
}

function quarantineStatusPresentation(status: QuarantineListItem['status']) {
  if (status === 'Released') return { label: '已放行', tone: 'success' as const }
  if (status === 'Deleted') return { label: '已删除', tone: 'neutral' as const }
  return { label: '待审核', tone: 'warning' as const }
}

const aliasLegacyModeLabels: Record<string, string> = { Disabled: '已停用' }
function aliasModeLabel(mode: AliasMode) {
  return aliasModeItems.find(item => item.value === mode)?.title ?? aliasLegacyModeLabels[mode] ?? mode
}

watch(() => route.params.tab, (value) => {
  const next = normalizeTab(value)
  if (next !== tab.value) tab.value = next
})
watch(tab, (value) => {
  const query = { ...route.query }
  delete query.tab
  delete query.section
  const clean = !('tab' in route.query) && !('section' in route.query)
  if (String(route.params.tab ?? '') === value && clean) return
  void router.replace({ name: 'domain-detail', params: { id: domainId.value, tab: value }, query })
}, { immediate: true })

watch([tab, domainId], ([value]) => {
  if (value === 'quarantine') void loadQuarantine()
  else if (value === 'audit') void loadAudit()
  else if (value === 'queue') queueRefreshKey.value++
}, { immediate: true })

watch(quarantineStatus, () => { quarantinePage.value = 1; void loadQuarantine() })
watch(quarantinePage, () => { void loadQuarantine() })
watch(quarantineSearch, () => {
  globalThis.clearTimeout(quarantineTimer)
  quarantineTimer = globalThis.setTimeout(() => { quarantinePage.value = 1; void loadQuarantine() }, 300)
})

watch(accountSearch, () => {
  globalThis.clearTimeout(accountTimer)
  accountTimer = globalThis.setTimeout(() => void loadAccounts(), 300)
})
watch(accountEnabled, () => void loadAccounts())
watch(aliasSearch, () => {
  globalThis.clearTimeout(aliasTimer)
  aliasTimer = globalThis.setTimeout(() => void loadAliases(), 300)
})
onMounted(loadPage)
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/domains">域与邮箱</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>{{ domain?.name || '域详情' }}</span>
  </div>

  <v-progress-linear v-if="loading && !domain" indeterminate color="primary" class="mb-5" />
  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>

  <template v-if="domain && health">
    <PageHeader :title="domain.name" :description="`${domain.mailHostName} · ${health.accountCount} 个邮箱账号`">
      <StatusPill :label="domain.isActive ? '运行中' : '已停用'" :tone="domain.isActive ? 'success' : 'neutral'" />
      <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="loading" @click="loadPage">刷新</v-btn>
      <v-btn color="primary" prepend-icon="mdi-stethoscope" :loading="dnsLoading" @click="runDnsCheck">Direct MX 检查</v-btn>
      <v-btn v-if="can('domain.manage', domainId)" variant="outlined" :prepend-icon="domain.isActive ? 'mdi-pause-circle-outline' : 'mdi-play-circle-outline'" :loading="saving" @click="toggleDomainActive">{{ domain.isActive ? '停用域' : '启用域' }}</v-btn>
      <v-btn v-if="can('domain.manage', domainId)" variant="outlined" color="error" prepend-icon="mdi-delete-outline" @click="openDomainDelete">删除域</v-btn>
    </PageHeader>

    <section class="domain-summary" aria-label="域运行摘要">
      <div><span>邮箱账号</span><strong>{{ health.enabledAccountCount }} / {{ health.accountCount }}</strong><small>{{ health.accountCount - health.enabledAccountCount }} 个已停用</small></div>
      <div><span>存储使用</span><strong>{{ bytes(domain.usedBytes) }}</strong><small>配额 {{ bytes(domain.quotaBytes) }}</small></div>
      <div><span>待投递 / 失败</span><strong>{{ health.pendingDeliveries }} / {{ health.failedDeliveries }}</strong><small>Direct MX 队列</small></div>
      <div :class="{ 'is-clickable': can('domain.quarantine.read', domainId) }" @click="can('domain.quarantine.read', domainId) && (tab = 'quarantine')"><span>隔离待审</span><strong>{{ health.pendingQuarantineCount }}</strong><small>{{ can('domain.quarantine.read', domainId) ? '点击前往隔离审核' : health.aliasCount + ' 个邮箱别名' }}</small></div>
      <div><span>证书</span><strong>{{ certificate ? '已配置' : '未配置' }}</strong><small>{{ certificate ? `到期 ${new Date(certificate.notAfter).toLocaleDateString('zh-CN')}` : 'TLS 服务需要证书' }}</small></div>
    </section>

    <v-tabs v-model="tab" class="detail-tabs" color="primary" show-arrows>
      <v-tab value="overview">概览</v-tab>
      <v-tab value="accounts">邮箱账号</v-tab>
      <v-tab value="aliases">别名</v-tab>
      <v-tab value="rules">收发信规则</v-tab>
      <v-tab v-if="can('domain.quarantine.read', domainId)" value="quarantine">隔离审核</v-tab>
      <v-tab value="queue">投递队列</v-tab>
      <v-tab value="audit">域审计</v-tab>
      <v-tab value="dns">DNS 与安全</v-tab>
      <v-tab value="cert">证书与连接</v-tab>
      <v-tab value="quota">配额</v-tab>
    </v-tabs>

    <section v-if="tab === 'overview'" class="domain-workspace">
      <v-card flat class="panel readiness-panel">
        <div class="panel-head">
          <div><h2>Direct MX 就绪性</h2><p>DNS 身份、传输安全与公网可达性检查</p></div>
          <StatusPill v-bind="dnsStatusView(dnsCheck?.status)" />
        </div>
        <div class="readiness-list">
          <div v-for="item in dnsCheck?.validation?.items ?? []" :key="`${item.kind}-${item.name}`">
            <v-icon :color="item.status === 'match' ? 'success' : 'error'" size="20">{{ item.status === 'match' ? 'mdi-check-circle-outline' : 'mdi-alert-circle-outline' }}</v-icon>
            <span><b>{{ item.kind }}</b><small>{{ item.name }}</small></span>
            <span class="readiness-value">{{ item.actual.join(' · ') || '未发布' }}</span>
          </div>
          <div v-for="warning in dnsCheck?.validation?.warnings ?? []" :key="warning" class="is-warning">
            <v-icon color="warning" size="20">mdi-alert-outline</v-icon>
            <span><b>交付提示</b><small>不阻止外发</small></span>
            <span class="readiness-value">{{ warning }}</span>
          </div>
          <div v-if="!dnsCheck?.validation" class="readiness-empty"><span>尚无检查结果</span><v-btn size="small" variant="tonal" color="primary" @click="runDnsCheck">立即检查</v-btn></div>
        </div>
        <div class="panel-action"><span>最近检查：{{ date(dnsCheck?.checkedAt) }}</span><v-btn variant="text" color="primary" @click="tab = 'dns'">查看完整结果</v-btn></div>
      </v-card>

      <v-card flat class="panel domain-info-panel">
        <div class="panel-head"><div><h2>域配置</h2><p>邮件主机与运行参数</p></div><v-btn v-if="can('domain.manage', domainId)" icon="mdi-pencil-outline" variant="text" aria-label="编辑域配置" @click="openDomainEdit" /></div>
        <dl class="domain-info-list">
          <div><dt>邮件主机</dt><dd>{{ domain.mailHostName }}</dd></div>
          <div><dt>SMTP 主机</dt><dd>{{ domain.smtpHostName || domain.mailHostName }}</dd></div>
          <div><dt>出站地址</dt><dd>{{ domain.outboundIpAddresses || '自动选择' }}</dd></div>
          <div><dt>DKIM 选择器</dt><dd>{{ domain.dkimSelector || '未配置' }}</dd></div>
          <div><dt>同域直投</dt><dd>{{ domain.directDeliverSameDomain ? '已启用' : '已停用' }}</dd></div>
          <div><dt>创建时间</dt><dd>{{ date(domain.createdAt) }}</dd></div>
          <div><dt>最近审计</dt><dd>{{ date(health.lastAuditAt) }}</dd></div>
        </dl>
      </v-card>

      <v-card flat class="panel connection-panel">
        <div class="panel-head"><div><h2>客户端连接</h2><p>可用于邮件客户端的服务端点</p></div></div>
        <div class="connection-list">
          <div v-for="endpoint in management?.connections ?? []" :key="`${endpoint.protocol}-${endpoint.port}`">
            <span class="endpoint-icon"><v-icon>{{ endpoint.protocol.startsWith('IMAP') ? 'mdi-inbox-arrow-down-outline' : 'mdi-send-outline' }}</v-icon></span>
            <span><b>{{ endpoint.protocol }}</b><small>{{ endpoint.description }}</small></span>
            <code>{{ endpoint.hostName }}:{{ endpoint.port }}</code>
            <StatusPill :label="endpoint.security === 'ImplicitTls' ? '隐式 TLS' : 'STARTTLS'" tone="success" />
          </div>
        </div>
      </v-card>
    </section>

    <section v-else-if="tab === 'accounts'" class="tab-panel panel">
      <div class="tab-toolbar">
        <div><h2>邮箱账号</h2><p>当前显示 {{ accountTotal }} 条预览记录，域总计 {{ health.accountCount }} 个账号</p></div>
        <div class="tab-toolbar__actions">
          <v-text-field v-model="accountSearch" placeholder="搜索邮箱地址" prepend-inner-icon="mdi-magnify" clearable hide-details />
          <v-select v-model="accountEnabled" :items="accountStatusItems" hide-details />
          <template v-if="can('domain.manage', domainId)">
            <v-btn variant="outlined" :disabled="!accountSelected.length || accountBusy" :loading="accountBusy" @click="batchAccounts('batch-enable')">批量启用</v-btn>
            <v-btn variant="outlined" :disabled="!accountSelected.length || accountBusy" :loading="accountBusy" @click="batchAccounts('batch-disable')">批量停用</v-btn>
            <v-btn variant="outlined" color="error" :disabled="!accountSelected.length || accountBusy" :loading="accountBusy" @click="batchAccounts('batch-delete')">批量删除</v-btn>
            <v-btn color="primary" prepend-icon="mdi-plus" @click="openAccountDialog">新建邮箱</v-btn>
          

</template>
        </div>
      </div>
      <v-table>
        <thead><tr>
          <th v-if="can('domain.manage', domainId)" class="select-column"><v-checkbox-btn :model-value="accountAllSelected" aria-label="选择当前页账号" @update:model-value="toggleAccountAll" /></th>
          <th>邮箱地址</th><th>状态</th><th>存储使用</th><th>别名</th><th>创建时间</th><th class="actions-column">操作</th>
        </tr></thead>
        <tbody>
          <tr v-if="accountLoading"><td :colspan="can('domain.manage', domainId) ? 7 : 6"><v-progress-linear indeterminate color="primary" /></td></tr>
          <tr v-else-if="!accounts.length"><td :colspan="can('domain.manage', domainId) ? 7 : 6" class="empty-row">没有符合条件的邮箱账号</td></tr>
          <tr v-for="account in accounts" v-else :key="account.id">
            <td v-if="can('domain.manage', domainId)" class="select-column"><v-checkbox-btn :model-value="accountSelected.includes(account.id)" :aria-label="`选择 ${account.address}`" @update:model-value="value => toggleAccount(account.id, value)" /></td>
            <td><b>{{ account.address }}</b><small class="row-id">{{ account.id.slice(0, 8) }}</small></td>
            <td><StatusPill :label="account.isEnabled ? '已启用' : '已停用'" :tone="account.isEnabled ? 'success' : 'neutral'" /></td>
            <td><div class="row-quota"><v-progress-linear :model-value="account.quotaBytes ? account.usedBytes / account.quotaBytes * 100 : 0" height="5" rounded /><small>{{ bytes(account.usedBytes) }} / {{ bytes(account.quotaBytes) }}</small></div></td>
            <td>{{ account.aliasCount }}</td><td>{{ date(account.createdAt) }}</td>
            <td class="actions-column">
              <v-tooltip v-if="can('domain.manage', domainId)" :text="account.isEnabled ? '停用账号' : '启用账号'" location="top">
                <template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" :icon="account.isEnabled ? 'mdi-pause-circle-outline' : 'mdi-play-circle-outline'" size="small" variant="text" :aria-label="account.isEnabled ? '停用账号' : '启用账号'" :disabled="accountBusy" @click="toggleAccountEnabled(account)" /></template>
              </v-tooltip>
              <v-tooltip v-if="can('domain.manage', domainId)" text="编辑配额与状态" location="top">
                <template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-pencil-outline" size="small" variant="text" aria-label="编辑账号" :disabled="accountBusy" @click="openAccountEdit(account)" /></template>
              </v-tooltip>
              <v-tooltip v-if="can('domain.manage', domainId)" text="重置登录密码" location="top">
                <template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-lock-reset" size="small" variant="text" aria-label="重置密码" :disabled="accountBusy" @click="openAccountPassword(account)" /></template>
              </v-tooltip>
              <v-tooltip v-if="can('domain.manage', domainId)" :text="account.isEnabled ? '请先停用账号再删除' : '软删除账号'" location="top">
                <template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-delete-outline" size="small" variant="text" color="error" aria-label="删除账号" :disabled="accountBusy || account.isEnabled" @click="deleteAccount(account)" /></template>
              </v-tooltip>
            </td>
          </tr>
        </tbody>
      </v-table>
    </section>

    <section v-else-if="tab === 'aliases'" class="aliases-workspace">
      <v-card flat class="panel alias-panel">
        <div class="tab-toolbar">
          <div><h2>邮箱别名</h2><p>将其他地址绑定到真实邮箱账号，可选「可收可发」或「仅收信」</p></div>
          <div class="tab-toolbar__actions">
            <v-text-field v-model="aliasSearch" placeholder="搜索别名或目标" prepend-inner-icon="mdi-magnify" clearable hide-details />
            <template v-if="can('domain.manage', domainId)">
              <v-btn variant="outlined" :disabled="!aliasSelected.length || aliasBusy" :loading="aliasBusy" @click="batchAliasMode('SendReceive')">设为可收可发</v-btn>
              <v-btn variant="outlined" :disabled="!aliasSelected.length || aliasBusy" :loading="aliasBusy" @click="batchAliasMode('ReceiveOnly')">设为仅收信</v-btn>
              <v-btn variant="outlined" color="error" :disabled="!aliasSelected.length || aliasBusy" :loading="aliasBusy" @click="batchAliasDelete">批量删除</v-btn>
              <v-btn color="primary" prepend-icon="mdi-plus" @click="openAliasDialog">新建别名</v-btn>
            </template>
          </div>
        </div>
        <v-table>
          <thead><tr>
            <th v-if="can('domain.manage', domainId)" class="select-column"><v-checkbox-btn :model-value="aliasAllSelected" aria-label="选择当前页别名" @update:model-value="toggleAliasAll" /></th>
            <th>别名地址</th><th>目标邮箱</th><th>模式</th><th>状态</th><th class="actions-column">操作</th>
          </tr></thead>
          <tbody>
            <tr v-if="aliasLoading"><td :colspan="can('domain.manage', domainId) ? 6 : 5"><v-progress-linear indeterminate color="primary" /></td></tr>
            <tr v-else-if="!aliases.length"><td :colspan="can('domain.manage', domainId) ? 6 : 5" class="empty-row">没有符合条件的邮箱别名</td></tr>
            <tr v-for="alias in aliases" v-else :key="alias.id">
              <td v-if="can('domain.manage', domainId)" class="select-column"><v-checkbox-btn :model-value="aliasSelected.includes(alias.id)" :aria-label="`选择 ${alias.source}`" @update:model-value="value => toggleAlias(alias.id, value)" /></td>
              <td><b>{{ alias.source }}</b></td><td>{{ alias.targetAddress || '未绑定' }}</td><td>{{ aliasModeLabel(alias.mode) }}</td><td><StatusPill :label="alias.isEnabled ? '已启用' : '已停用'" :tone="alias.isEnabled ? 'success' : 'neutral'" /></td>
              <td class="actions-column">
                <v-tooltip v-if="can('domain.manage', domainId)" text="改绑目标或调整模式" location="top">
                  <template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-pencil-outline" size="small" variant="text" aria-label="编辑别名" :disabled="aliasBusy" @click="openAliasEdit(alias)" /></template>
                </v-tooltip>
                <v-tooltip v-if="can('domain.manage', domainId)" text="删除别名" location="top">
                  <template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-delete-outline" size="small" variant="text" color="error" aria-label="删除别名" :disabled="aliasBusy" @click="deleteAlias(alias)" /></template>
                </v-tooltip>
              </td>
            </tr>
          </tbody>
        </v-table>
      </v-card>
    </section>

    <DomainRuleWorkspace
      v-else-if="tab === 'rules'"
      :domain-id="domainId"
      :domain-name="domain.name"
      :can-manage="can('domain.manage', domainId)"
    />

    <section v-else-if="tab === 'quarantine'" class="tab-panel panel">
      <div class="tab-toolbar">
        <div><h2>隔离审核</h2><p>被安全插件拦截的邮件在此人工放行或删除，共 {{ quarantineTotal }} 条记录</p></div>
        <div class="tab-toolbar__actions">
          <v-text-field v-model="quarantineSearch" placeholder="搜索发件人、收件人或原因" prepend-inner-icon="mdi-magnify" clearable hide-details />
          <v-select v-model="quarantineStatus" :items="quarantineStatusItems" hide-details />
          <v-btn icon="mdi-refresh" variant="text" aria-label="刷新隔离列表" :loading="quarantineLoading" @click="loadQuarantine" />
        </div>
      </div>
      <v-table>
        <thead><tr><th>发件人与原因</th><th>收件人</th><th>来源</th><th>大小</th><th>隔离时间</th><th>状态</th><th class="actions-column">操作</th></tr></thead>
        <tbody>
          <tr v-if="quarantineLoading"><td colspan="7"><v-progress-linear indeterminate color="primary" /></td></tr>
          <tr v-else-if="!quarantineRows.length"><td colspan="7" class="empty-row">没有符合条件的隔离邮件</td></tr>
          <tr v-for="item in quarantineRows" v-else :key="item.id">
            <td class="quarantine-reason"><b>{{ item.sender }}</b><small>{{ item.reason }}</small><small v-if="item.pluginName" class="row-id">{{ item.pluginName }}</small></td>
            <td>{{ item.recipients.join(', ') }}</td>
            <td><code>{{ item.remoteAddress }}</code></td>
            <td>{{ bytes(item.size) }}</td>
            <td>{{ date(item.quarantinedAt) }}</td>
            <td><StatusPill v-bind="quarantineStatusPresentation(item.status)" /></td>
            <td class="actions-column">
              <v-btn size="small" variant="text" prepend-icon="mdi-file-eye-outline" @click="openQuarantineDetail(item)">查看原文</v-btn>
              <template v-if="can('domain.quarantine.manage', domainId) && item.status === 'PendingReview'">
                <v-btn size="small" variant="text" color="primary" prepend-icon="mdi-email-check-outline" :disabled="quarantineBusy" @click="requestQuarantineAction(item, 'release')">放行</v-btn>
                <v-btn size="small" variant="text" color="error" prepend-icon="mdi-delete-outline" :disabled="quarantineBusy" @click="requestQuarantineAction(item, 'delete')">删除</v-btn>
              </template>
            </td>
          </tr>
        </tbody>
      </v-table>
      <div class="table-footer"><span>第 {{ quarantinePage }} / {{ quarantinePageCount }} 页 · 每页 {{ quarantinePageSize }} 条</span><v-pagination v-model="quarantinePage" :length="quarantinePageCount" density="compact" /></div>
    </section>

    <section v-else-if="tab === 'queue'" class="tab-panel">
      <QueueWorkspace mode="queue" :refresh-key="queueRefreshKey" :domain-id="domainId" />
    </section>

    <section v-else-if="tab === 'audit'" class="tab-panel panel">
      <div class="tab-toolbar">
        <div><h2>域审计</h2><p>该域最近的账号、别名、规则、DNS 与证书变更记录</p></div>
        <div class="tab-toolbar__actions"><v-btn icon="mdi-refresh" variant="text" aria-label="刷新域审计" :loading="auditLoading" @click="loadAudit" /></div>
      </div>
      <v-table>
        <thead><tr><th>时间</th><th>操作者</th><th>动作</th><th>资源</th><th>详情</th></tr></thead>
        <tbody>
          <tr v-if="auditLoading"><td colspan="5"><v-progress-linear indeterminate color="primary" /></td></tr>
          <tr v-else-if="!auditRows.length"><td colspan="5" class="empty-row">尚无域级审计记录</td></tr>
          <tr v-for="entry in auditRows" v-else :key="entry.id">
            <td>{{ date(entry.createdAt) }}</td><td>{{ entry.actor }}</td><td><code>{{ entry.action }}</code></td><td><code>{{ entry.resource }}</code></td><td class="quarantine-reason">{{ entry.detail || '—' }}</td>
          </tr>
        </tbody>
      </v-table>
    </section>

    <section v-else-if="tab === 'dns'" class="dns-tab">
      <DomainDnsWorkspace
        :domain-id="domainId"
        :domain-name="domain.name"
        :mail-host-name="domain.mailHostName"
        :dkim-selector="domain.dkimSelector"
        :dkim-public-key="domain.dkimPublicKey"
        :dns-check="dnsCheck"
        :dns-loading="dnsLoading"
        :can-manage="can('domain.manage', domainId)"
        @saved="onDnsSaved"
        @dkim-generated="onDkimGenerated"
        @check="runDnsCheck"
        @validate="validateDns"
      />
    </section>
    <DomainCertificateWorkspace
      v-else-if="tab === 'cert'"
      :domain-id="domainId"
      :domain-name="domain.name"
      :mail-host-name="domain.mailHostName"
      :certificate="certificate"
      :outbound-addresses="management?.outboundAddressOptions ?? []"
      :can-manage="can('domain.manage', domainId)"
      @changed="onCertificateChanged"
    />

    <section v-else class="quota-workspace">
      <v-card flat class="panel quota-panel">
        <div class="panel-head"><div><h2>域存储配额</h2><p>限制该域全部邮箱和邮件内容的总存储量</p></div></div>
        <div class="quota-overview"><span><strong>{{ bytes(domain.usedBytes) }}</strong><small>已使用</small></span><span><strong>{{ quotaPercent.toFixed(1) }}%</strong><small>使用率</small></span><span><strong>{{ bytes(domain.quotaBytes - domain.usedBytes) }}</strong><small>当前剩余</small></span></div>
        <v-progress-linear :model-value="quotaPercent" height="10" rounded color="primary" class="quota-progress" />
        <div class="quota-editor"><v-text-field v-model.number="quotaGiB" type="number" label="新配额（GiB）" :min="minimumQuotaGiB" max="102400" suffix="GiB" :error-messages="quotaGiB < minimumQuotaGiB ? `不能低于当前已用存储，至少 ${minimumQuotaGiB} GiB` : ''" /><div><span>允许范围 {{ minimumQuotaGiB }} 至 102400 GiB</span><v-btn v-if="can('domain.manage', domainId)" color="primary" :disabled="!quotaValid || quotaGiB === Math.round(domain.quotaBytes / 1024 ** 3)" :loading="saving" @click="saveQuota">保存配额</v-btn></div></div>
      </v-card>
    </section>
  </template>

  <v-dialog v-model="domainEditOpen" max-width="560" :persistent="saving">
    <v-card title="编辑域配置"><v-card-text><v-alert v-if="dialogError" type="error" variant="tonal" class="mb-4">{{ dialogError }}</v-alert><v-text-field v-model="domainDraft.mailHostName" label="邮件主机名" placeholder="mail.example.com" /><v-switch v-model="domainDraft.isActive" label="启用该域" hide-details /><p class="dialog-hint">停用后不再接受该域的新协议会话，但不会删除现有邮件数据。</p></v-card-text><v-card-actions><v-spacer /><v-btn :disabled="saving" @click="domainEditOpen = false">取消</v-btn><v-btn color="primary" :loading="saving" :disabled="!domainDraft.mailHostName.trim()" @click="saveDomain">保存配置</v-btn></v-card-actions></v-card>
  </v-dialog>

  <v-dialog v-model="accountDialogOpen" max-width="580" :persistent="saving">
    <v-card title="新建邮箱账号"><v-card-text><v-alert v-if="dialogError" type="error" variant="tonal" class="mb-4">{{ dialogError }}</v-alert><v-text-field v-model="accountDraft.localPart" label="邮箱本地部分" :suffix="`@${domain?.name}`" placeholder="name" autocomplete="off" /><v-text-field v-model="accountDraft.password" label="初始密码" type="password" autocomplete="new-password" hint="至少 12 个字符" persistent-hint /><v-text-field v-model.number="accountDraft.quotaGiB" label="账号配额" type="number" min="1" max="10240" suffix="GiB" /></v-card-text><v-card-actions><v-spacer /><v-btn :disabled="saving" @click="accountDialogOpen = false">取消</v-btn><v-btn color="primary" :disabled="!accountValid" :loading="saving" @click="createAccount">创建邮箱</v-btn></v-card-actions></v-card>
  </v-dialog>

  <v-dialog v-model="aliasDialogOpen" max-width="580" :persistent="saving">
    <v-card title="新建邮箱别名"><v-card-text><v-alert v-if="dialogError" type="error" variant="tonal" class="mb-4">{{ dialogError }}</v-alert><v-text-field v-model="aliasDraft.sourceLocalPart" label="别名本地部分" :suffix="`@${domain?.name}`" placeholder="sales" autocomplete="off" /><v-select v-model="aliasDraft.targetAccountId" :items="accountOptions" label="目标邮箱" /><v-select v-model="aliasDraft.mode" :items="aliasModeItems" label="使用模式" /><v-alert type="info" variant="tonal" density="compact">别名不会创建独立邮箱存储，邮件会投递到目标账号。</v-alert></v-card-text><v-card-actions><v-spacer /><v-btn :disabled="saving" @click="aliasDialogOpen = false">取消</v-btn><v-btn color="primary" :disabled="!aliasValid" :loading="saving" @click="createAlias">创建别名</v-btn></v-card-actions></v-card>
  </v-dialog>
  <v-dialog v-model="accountEditOpen" max-width="560" :persistent="accountBusy">
    <v-card title="编辑邮箱账号">
      <v-card-text>
        <v-alert v-if="dialogError" type="error" variant="tonal" class="mb-4">{{ dialogError }}</v-alert>
        <p class="dialog-target">{{ accountEditTarget?.address }}</p>
        <v-switch v-model="accountEditDraft.isEnabled" label="启用该邮箱账号" hide-details class="mb-2" />
        <v-text-field v-model.number="accountEditDraft.quotaGiB" type="number" label="账号配额" :min="1" max="10240" suffix="GiB" :error-messages="accountEditValid ? '' : `不能低于已用 ${bytes(accountEditTarget?.usedBytes ?? 0)}，且不超过域配额`" />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn :disabled="accountBusy" @click="accountEditOpen = false">取消</v-btn><v-btn color="primary" :disabled="!accountEditValid" :loading="accountBusy" @click="saveAccountEdit">保存账号</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog v-model="accountPasswordOpen" max-width="560" :persistent="accountBusy">
    <v-card title="重置邮箱密码">
      <v-card-text>
        <v-alert v-if="dialogError" type="error" variant="tonal" class="mb-4">{{ dialogError }}</v-alert>
        <v-alert type="warning" variant="tonal" density="compact" class="mb-4">重置后客户端需使用新密码重新登录，正在同步的会话可能中断。</v-alert>
        <p class="dialog-target">{{ accountPasswordTarget?.address }}</p>
        <v-text-field v-model="accountPasswordDraft.password" type="password" label="新密码" autocomplete="new-password" hint="至少 12 个字符" persistent-hint />
        <v-text-field v-model="accountPasswordDraft.confirm" type="password" label="确认新密码" autocomplete="new-password" :error-messages="accountPasswordDraft.confirm && accountPasswordDraft.password !== accountPasswordDraft.confirm ? '两次输入的密码不一致' : ''" />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn :disabled="accountBusy" @click="accountPasswordOpen = false">取消</v-btn><v-btn color="primary" :disabled="!accountPasswordValid" :loading="accountBusy" @click="saveAccountPassword">重置密码</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog v-model="aliasEditOpen" max-width="580" :persistent="aliasBusy">
    <v-card title="编辑邮箱别名">
      <v-card-text>
        <v-alert v-if="dialogError" type="error" variant="tonal" class="mb-4">{{ dialogError }}</v-alert>
        <p class="dialog-target">{{ aliasEditTarget?.source }}</p>
        <v-text-field v-model="aliasEditDraft.sourceLocalPart" label="别名本地部分" :suffix="`@${domain?.name}`" autocomplete="off" />
        <v-select v-model="aliasEditDraft.targetAccountId" :items="accountOptions" label="目标邮箱" />
        <v-select v-model="aliasEditDraft.mode" :items="aliasModeItems" label="使用模式" />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn :disabled="aliasBusy" @click="aliasEditOpen = false">取消</v-btn><v-btn color="primary" :disabled="!aliasEditValid" :loading="aliasBusy" @click="saveAliasEdit">保存别名</v-btn></v-card-actions>
    </v-card>
  </v-dialog>


  <v-dialog v-model="quarantineDetailOpen" max-width="900" scrollable>
    <v-card title="隔离邮件原文">
      <v-card-text>
        <v-progress-linear v-if="quarantineDetailLoading" indeterminate color="primary" />
        <template v-else-if="quarantineDetail">
          <div class="quarantine-facts">
            <div><span>发件人</span><b>{{ quarantineDetail.record.sender }}</b></div>
            <div><span>收件人</span><b>{{ quarantineDetail.record.recipients.join(', ') }}</b></div>
            <div><span>来源地址</span><b>{{ quarantineDetail.record.remoteAddress }}</b></div>
            <div><span>命中原因</span><b>{{ quarantineDetail.record.reason }}</b></div>
          </div>
          <pre class="quarantine-raw">{{ quarantineDetail.content }}</pre>
        </template>
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn @click="quarantineDetailOpen = false">关闭</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="!!quarantineActionKind" max-width="560" persistent @update:model-value="value => { if (!value && !quarantineBusy) { quarantineActionKind = null; quarantineActionTarget = null } }">
    <v-card v-if="quarantineActionTarget" :title="quarantineActionKind === 'release' ? '确认放行隔离邮件' : '确认删除隔离邮件'">
      <v-card-text>
        <v-alert :type="quarantineActionKind === 'release' ? 'info' : 'warning'" variant="tonal" class="mb-4">{{ quarantineActionKind === 'release' ? '放行后邮件会重新进入投递流程并送达原始收件人。' : '删除会永久移除隔离邮件，无法从回收站恢复。' }}</v-alert>
        <p class="dialog-target">{{ quarantineActionTarget.sender }} → {{ quarantineActionTarget.recipients.join(', ') }}</p>
        <v-textarea v-model="quarantineNote" label="审核备注（可选）" rows="3" auto-grow :readonly="quarantineBusy" />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn :disabled="quarantineBusy" @click="quarantineActionKind = null">取消</v-btn><v-btn :color="quarantineActionKind === 'release' ? 'primary' : 'error'" :loading="quarantineBusy" @click="confirmQuarantineAction">确认{{ quarantineActionKind === 'release' ? '放行' : '删除' }}</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog v-model="domainDeleteOpen" max-width="560" :persistent="domainDeleteBusy">
    <v-card title="删除邮件域">
      <v-card-text>
        <v-alert type="error" variant="tonal" class="mb-4">删除后该域的邮箱、别名、规则与证书会进入清理流程，客户端将立即无法收发邮件。</v-alert>
        <v-alert v-if="dialogError" type="error" variant="tonal" class="mb-4">{{ dialogError }}</v-alert>
        <p class="mb-3">请输入域名「{{ domain?.name }}」确认删除。</p>
        <v-text-field v-model="domainDeleteConfirm" label="确认域名" autocomplete="off" :readonly="domainDeleteBusy" autofocus />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn :disabled="domainDeleteBusy" @click="domainDeleteOpen = false">取消</v-btn><v-btn color="error" :loading="domainDeleteBusy" :disabled="domainDeleteConfirm !== domain?.name" @click="deleteDomain">确认删除</v-btn></v-card-actions>
    </v-card>
  </v-dialog>


</template>

<style scoped>
.domain-summary { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); margin: -4px 0 20px; overflow: hidden; background: #fff; border: 1px solid var(--border); border-radius: 8px; }
.domain-summary > div { display: flex; min-width: 0; flex-direction: column; gap: 4px; padding: 15px 18px; border-right: 1px solid var(--border); }
.domain-summary > div:last-child { border-right: 0; }
.domain-summary span { color: var(--text-muted); font-size: 11px; }
.domain-summary strong { overflow: hidden; color: var(--text); font-size: 18px; text-overflow: ellipsis; white-space: nowrap; }
.domain-summary small { overflow: hidden; color: var(--text-secondary); font-size: 11px; text-overflow: ellipsis; white-space: nowrap; }
.detail-tabs { margin-bottom: 18px; background: #fff; border: 1px solid var(--border); border-radius: 8px; }
.domain-workspace { display: grid; grid-template-columns: minmax(0, 1.65fr) minmax(330px, .75fr); gap: 16px; }
.readiness-panel { min-width: 0; }
.readiness-list { padding: 0 20px; }
.readiness-list > div { display: grid; grid-template-columns: auto minmax(120px, .45fr) minmax(240px, 1fr); gap: 12px; align-items: center; min-height: 58px; border-top: 1px solid var(--border); }
.readiness-list > div:first-child { border-top: 0; }
.readiness-list span:not(.readiness-value) { display: flex; min-width: 0; flex-direction: column; }
.readiness-list b { color: var(--text); font-size: 13px; }
.readiness-list small { color: var(--text-muted); font-size: 10px; }
.readiness-value { overflow: hidden; color: var(--text-secondary); font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 11px; text-overflow: ellipsis; white-space: nowrap; }
.readiness-list .is-warning { background: #fffbf2; }
.readiness-empty { grid-template-columns: 1fr auto !important; padding-block: 18px; color: var(--text-muted); }
.panel-action { display: flex; align-items: center; justify-content: space-between; gap: 16px; min-height: 52px; padding: 8px 20px; color: var(--text-muted); border-top: 1px solid var(--border); font-size: 11px; }
.domain-info-panel { min-width: 0; }
.domain-info-list, .policy-list { margin: 0; padding: 0 20px 16px; }
.domain-info-list div, .policy-list div { display: grid; grid-template-columns: 105px minmax(0, 1fr); gap: 14px; padding: 11px 0; border-top: 1px solid var(--border); }
.domain-info-list dt, .policy-list dt { color: var(--text-muted); font-size: 11px; }
.domain-info-list dd, .policy-list dd { margin: 0; overflow-wrap: anywhere; color: var(--text); font-size: 12px; font-weight: 600; }
.connection-panel { grid-column: 1 / -1; }
.connection-list > div { display: grid; grid-template-columns: auto minmax(160px, 1fr) minmax(240px, .8fr) auto; gap: 14px; align-items: center; min-height: 66px; padding: 10px 20px; border-top: 1px solid var(--border); }
.endpoint-icon { display: grid; width: 36px; height: 36px; place-items: center; color: var(--color-primary); background: var(--color-primary-soft); border-radius: 7px; }
.connection-list span:nth-child(2) { display: flex; flex-direction: column; }
.connection-list b { color: var(--text); font-size: 12px; }
.connection-list small { color: var(--text-muted); font-size: 10px; }
.connection-list code { color: var(--text-secondary); font-size: 11px; }
.tab-panel { overflow: hidden; }
.tab-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 20px; min-height: 76px; padding: 14px 20px; border-bottom: 1px solid var(--border); }
.tab-toolbar h2 { margin: 0 0 3px; color: var(--text); font-size: 15px; }
.tab-toolbar p { margin: 0; color: var(--text-muted); font-size: 11px; }
.tab-toolbar__actions { display: flex; align-items: center; gap: 9px; }
.tab-toolbar__actions :deep(.v-text-field) { width: 240px; }
.tab-toolbar__actions :deep(.v-select) { width: 150px; }
.row-id { display: block; margin-top: 3px; color: var(--text-muted); font-family: monospace; font-size: 10px; }
.row-quota { width: 190px; }
.row-quota small { display: block; margin-top: 5px; color: var(--text-muted); font-size: 10px; }
.empty-row { height: 110px; color: var(--text-muted) !important; text-align: center; }
.aliases-workspace, .rules-workspace { display: grid; gap: 16px; }
.dns-tab { display: grid; gap: 16px; }
.alias-panel { overflow: hidden; }
.rule-priority-legacy { display: grid; width: 28px; height: 28px; place-items: center; color: #436385; background: #edf3f9; border-radius: 6px; font-size: 11px; font-weight: 700; }
.rules-list-legacy span:nth-child(2) { display: flex; min-width: 0; flex-direction: column; }
.rules-list-legacy b { color: var(--text); font-size: 12px; }
.rules-list-legacy small { overflow: hidden; color: var(--text-muted); font-size: 10px; text-overflow: ellipsis; white-space: nowrap; }
.quota-workspace { max-width: 920px; }
.quota-panel { padding-bottom: 22px; }
.quota-overview { display: grid; grid-template-columns: repeat(3, 1fr); margin: 0 22px; border: 1px solid var(--border); border-radius: 8px; }
.quota-overview > span { display: flex; flex-direction: column; gap: 4px; padding: 16px 18px; border-right: 1px solid var(--border); }
.quota-overview > span:last-child { border-right: 0; }
.quota-overview strong { color: var(--text); font-size: 21px; }
.quota-overview small { color: var(--text-muted); font-size: 11px; }
.quota-progress { margin: 20px 22px; width: calc(100% - 44px); }
.quota-editor { margin: 0 22px; padding-top: 18px; border-top: 1px solid var(--border); }
.quota-editor :deep(.v-text-field) { max-width: 320px; }
.quota-editor > div { display: flex; align-items: center; justify-content: space-between; gap: 20px; }
.quota-editor > div span { color: var(--text-muted); font-size: 11px; }
.dialog-hint { margin: 14px 0 0; color: var(--text-muted); font-size: 11px; line-height: 1.6; }
@media (max-width: 1280px) {
  .domain-summary { grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .domain-summary > div:nth-child(3) { border-right: 0; }
  .domain-summary > div:nth-child(-n + 3) { border-bottom: 1px solid var(--border); }
  .domain-workspace { grid-template-columns: 1fr; }
  .connection-panel { grid-column: auto; }
}
@media (max-width: 850px) {
  .domain-summary { grid-template-columns: 1fr 1fr; }
  .domain-summary > div { border-right: 1px solid var(--border) !important; border-bottom: 1px solid var(--border); }
  .domain-summary > div:nth-child(2n) { border-right: 0 !important; }
  .tab-toolbar { align-items: stretch; flex-direction: column; }
  .tab-toolbar__actions { align-items: stretch; flex-wrap: wrap; }
  .tab-toolbar__actions :deep(.v-text-field), .tab-toolbar__actions :deep(.v-select) { width: 100%; }
  .readiness-list > div { grid-template-columns: auto 1fr; padding-block: 10px; }
  .readiness-value { grid-column: 2; white-space: normal; }
  .connection-list > div { grid-template-columns: auto 1fr auto; }
  .connection-list code { grid-column: 2; }
}

.domain-summary > div.is-clickable { cursor: pointer; }
.domain-summary > div.is-clickable:hover { background: var(--bg-app); }
.select-column { width: 52px; }
.actions-column { width: 172px; white-space: nowrap; }
.folder-coverage { display: flex; flex-wrap: wrap; gap: 12px; }
.folder-coverage > span { display: flex; align-items: center; gap: 5px; font-size: 11px; }
.folder-coverage b { color: var(--text); }
.quarantine-reason { max-width: 380px; }
.quarantine-reason b, .quarantine-reason small { display: block; }
.quarantine-reason small { margin-top: 3px; color: var(--text-muted); font-size: 11px; }
.dialog-target { margin: 0 0 12px; color: var(--text-secondary); font-size: 12px; font-weight: 600; }
.quarantine-facts { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 4px 18px; margin-bottom: 14px; }
.quarantine-facts span { display: block; color: var(--text-muted); font-size: 11px; }
.quarantine-facts b { overflow-wrap: anywhere; font-size: 12px; }
.quarantine-raw { max-height: 420px; margin: 0; padding: 14px; overflow: auto; background: var(--bg-app); border: 1px solid var(--border); border-radius: 7px; color: var(--text-secondary); font-size: 11px; line-height: 1.6; white-space: pre-wrap; }
</style>
