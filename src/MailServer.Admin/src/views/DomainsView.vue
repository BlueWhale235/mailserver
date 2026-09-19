<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api, jsonBody } from '../lib/api'
import PageHeader from '../components/PageHeader.vue'
import StatusPill from '../components/StatusPill.vue'
import { usePermission } from '../composables/usePermission'

interface Domain {
  id: string
  name: string
  mailHostName?: string
  isActive: boolean
  dnsStatus: 'PendingConfiguration' | 'Checking' | 'Healthy' | 'Unhealthy'
  dnsCheckedAt?: string
  accountCount: number
  usedBytes: number
  quotaBytes: number
  certificateNotAfter?: string
}

interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

const rows = ref<Domain[]>([])
const selected = ref<string[]>([])
const search = ref('')
const activeFilter = ref<boolean | null>(null)
const dnsFilter = ref<string | null>(null)
const page = ref(1)
const pageSize = 20
const totalCount = ref(0)
const loading = ref(false)
const error = ref('')
const batchLoading = ref(false)
const deleteOpen = ref(false)
const deleteConfirm = ref('')
const deleteBusy = ref(false)
const dialogError = ref('')
const { can } = usePermission()
let searchTimer: ReturnType<typeof globalThis.setTimeout> | undefined

const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))
const allSelected = computed(() => rows.value.length > 0 && rows.value.every(item => selected.value.includes(item.id)))
const activeItems = [
  { title: '全部状态', value: null },
  { title: '运行中', value: true },
  { title: '已停用', value: false },
]
const dnsItems = [
  { title: '全部就绪状态', value: null },
  { title: '已就绪', value: 'Healthy' },
  { title: '检查中', value: 'Checking' },
  { title: '需关注', value: 'Unhealthy' },
  { title: '未检查', value: 'PendingConfiguration' },
]

function queryString() {
  const query = new globalThis.URLSearchParams({ page: String(page.value), pageSize: String(pageSize) })
  if (search.value.trim()) query.set('search', search.value.trim())
  if (activeFilter.value !== null) query.set('isActive', String(activeFilter.value))
  if (dnsFilter.value) query.set('dnsStatus', dnsFilter.value)
  return query.toString()
}

async function loadDomains() {
  loading.value = true
  error.value = ''
  try {
    const result = await api<PagedResult<Domain>>(`/domains/search?${queryString()}`)
    rows.value = result.items
    totalCount.value = result.totalCount
    selected.value = selected.value.filter(id => result.items.some(item => item.id === id))
  } catch (cause) {
    rows.value = []
    totalCount.value = 0
    error.value = cause instanceof Error ? cause.message : '域列表加载失败'
  } finally {
    loading.value = false
  }
}

function toggleAll(value: boolean | null) {
  selected.value = value ? rows.value.map(item => item.id) : []
}

function toggleOne(id: string, value: boolean | null) {
  selected.value = value
    ? Array.from(new Set([...selected.value, id]))
    : selected.value.filter(item => item !== id)
}

async function runDnsChecks() {
  if (!selected.value.length) return
  batchLoading.value = true
  error.value = ''
  try {
    await api('/domains/batch/dns-check', {
      method: 'POST',
      body: jsonBody({ domainIds: selected.value }),
    })
    await loadDomains()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '批量 DNS 检查失败'
  } finally {
    batchLoading.value = false
  }
}

function openDelete() {
  deleteConfirm.value = ''
  dialogError.value = ''
  deleteOpen.value = true
}

async function confirmDelete() {
  if (!selected.value.length || deleteConfirm.value !== '删除所选域' || deleteBusy.value) return
  deleteBusy.value = true
  dialogError.value = ''
  try {
    await api('/domains/batch/delete', { method: 'POST', body: jsonBody({ domainIds: selected.value }) })
    deleteOpen.value = false
    selected.value = []
    await loadDomains()
  } catch (cause) {
    dialogError.value = cause instanceof Error ? cause.message : '批量删除失败'
  } finally {
    deleteBusy.value = false
  }
}

function dnsPresentation(status: Domain['dnsStatus']) {
  if (status === 'Healthy') return { label: '已就绪', tone: 'success' as const }
  if (status === 'Unhealthy') return { label: '需关注', tone: 'warning' as const }
  if (status === 'Checking') return { label: '检查中', tone: 'neutral' as const }
  return { label: '未检查', tone: 'neutral' as const }
}

function bytes(value: number) {
  if (!value) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1)
  return `${(value / 1024 ** index).toFixed(index > 2 ? 1 : 0)} ${units[index]}`
}

watch(search, () => {
  globalThis.clearTimeout(searchTimer)
  searchTimer = globalThis.setTimeout(() => { page.value = 1; void loadDomains() }, 350)
})
watch([activeFilter, dnsFilter], () => { page.value = 1; void loadDomains() })
watch(page, () => { void loadDomains() })
onMounted(loadDomains)
</script>

<template>
  <PageHeader title="域与邮箱" description="管理邮件域、邮箱账号、别名与 Direct MX 配置">
    <v-btn v-if="can('domain.manage')" variant="outlined" prepend-icon="mdi-dns-outline" :disabled="!selected.length" :loading="batchLoading" @click="runDnsChecks">检查所选域</v-btn>
    <v-btn v-if="can('domain.manage')" variant="outlined" color="error" prepend-icon="mdi-delete-outline" :disabled="!selected.length || batchLoading" @click="openDelete">删除所选</v-btn>
    <v-btn v-if="can('domain.manage')" color="primary" prepend-icon="mdi-plus" to="/domains/new">添加域</v-btn>
  </PageHeader>
  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>
  <v-card flat class="panel table-panel">
    <div class="table-toolbar">
      <v-text-field v-model="search" prepend-inner-icon="mdi-magnify" placeholder="搜索域名或邮件主机" hide-details density="compact" clearable />
      <v-select v-model="activeFilter" :items="activeItems" hide-details density="compact" />
      <v-select v-model="dnsFilter" :items="dnsItems" hide-details density="compact" />
      <v-spacer />
      <v-btn icon="mdi-refresh" variant="text" aria-label="刷新域列表" :loading="loading" @click="loadDomains" />
    </div>
    <v-table>
      <thead><tr><th><v-checkbox-btn :model-value="allSelected" aria-label="选择当前页" @update:model-value="toggleAll" /></th><th>域名</th><th>服务状态</th><th>DIRECT MX</th><th>邮箱</th><th>证书有效期</th><th>存储配额</th><th></th></tr></thead>
      <tbody>
        <tr v-if="loading"><td colspan="8"><v-progress-linear indeterminate color="primary" /></td></tr>
        <tr v-else-if="!rows.length"><td colspan="8" class="text-center py-10 text-medium-emphasis">没有符合条件的邮件域</td></tr>
        <tr v-for="domain in rows" v-else :key="domain.id">
          <td><v-checkbox-btn :model-value="selected.includes(domain.id)" :aria-label="`选择 ${domain.name}`" @update:model-value="value => toggleOne(domain.id, value)" /></td>
          <td><router-link :to="`/domains/${domain.id}`" class="domain-link">{{ domain.name }}</router-link><small>{{ domain.mailHostName || '未配置邮件主机' }}</small></td>
          <td><StatusPill :label="domain.isActive ? '运行中' : '已停用'" :tone="domain.isActive ? 'success' : 'neutral'" /></td>
          <td><StatusPill v-bind="dnsPresentation(domain.dnsStatus)" /><small v-if="domain.dnsCheckedAt">{{ new Date(domain.dnsCheckedAt).toLocaleString('zh-CN') }}</small></td>
          <td>{{ domain.accountCount }}</td>
          <td>{{ domain.certificateNotAfter ? new Date(domain.certificateNotAfter).toLocaleDateString('zh-CN') : '未配置' }}</td>
          <td><div class="quota"><v-progress-linear :model-value="domain.quotaBytes ? domain.usedBytes / domain.quotaBytes * 100 : 0" height="5" rounded /><small>{{ bytes(domain.usedBytes) }} / {{ bytes(domain.quotaBytes) }}</small></div></td>
          <td><v-btn :to="`/domains/${domain.id}`" icon="mdi-chevron-right" variant="text" :aria-label="`打开 ${domain.name}`" /></td>
        </tr>
      </tbody>
    </v-table>
    <div class="table-footer"><span>共 {{ totalCount }} 个域<span v-if="selected.length"> · 已选择 {{ selected.length }} 个</span></span><v-pagination v-model="page" :length="pageCount" density="compact" /></div>
  </v-card>

  <v-dialog v-model="deleteOpen" max-width="580" :persistent="deleteBusy">
    <v-card title="批量删除邮件域">
      <v-card-text>
        <v-alert type="error" variant="tonal" class="mb-4">即将删除 {{ selected.length }} 个邮件域。对应邮箱、别名、规则与证书会进入清理流程，客户端将立即无法收发邮件。</v-alert>
        <v-alert v-if="dialogError" type="error" variant="tonal" class="mb-4">{{ dialogError }}</v-alert>
        <p class="mb-3">请输入「删除所选域」确认操作。</p>
        <v-text-field v-model="deleteConfirm" label="确认文字" autocomplete="off" :readonly="deleteBusy" autofocus />
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn :disabled="deleteBusy" @click="deleteOpen = false">取消</v-btn><v-btn color="error" :loading="deleteBusy" :disabled="deleteConfirm !== '删除所选域'" @click="confirmDelete">确认删除</v-btn></v-card-actions>
    </v-card>
  </v-dialog>
</template>
