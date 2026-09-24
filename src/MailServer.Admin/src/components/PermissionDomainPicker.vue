<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '../lib/api'

interface DomainOption { id: string; name: string }
interface PagedResult<T> { items: T[]; totalCount: number; canSelectAll: boolean }

const props = defineProps<{ permissionCode: string; disabled?: boolean }>()
const selected = defineModel<string[]>({ required: true })
const searchDraft = ref('')
const search = ref('')
const page = ref(1)
const pageSize = 20
const totalCount = ref(0)
const canSelectAll = ref(false)
const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))
const items = ref<DomainOption[]>([])
const names = ref<Record<string, string>>({})
const loading = ref(false)
const error = ref('')
let version = 0
let active = true

const allSelected = computed(() => selected.value.includes('*'))
const specificIds = computed(() => selected.value.filter(id => id !== '*'))

async function loadPage() {
  const current = ++version
  loading.value = true
  error.value = ''
  try {
    const params = new URLSearchParams({ code: props.permissionCode, page: String(page.value), pageSize: String(pageSize) })
    if (search.value) params.set('search', search.value)
    const result = await api<PagedResult<DomainOption>>(`/permissions/domains?${params}`)
    if (!active || current !== version) return
    totalCount.value = result.totalCount
    canSelectAll.value = result.canSelectAll
    if (page.value > pageCount.value) {
      page.value = pageCount.value
      await loadPage()
      return
    }
    items.value = result.items
    for (const domain of result.items) names.value[domain.id] = domain.name
  } catch (cause) {
    if (!active || current !== version) return
    items.value = []
    error.value = cause instanceof Error ? cause.message : '域列表加载失败'
  } finally {
    if (active && current === version) loading.value = false
  }
}

async function resolveSelected(ids: string[]) {
  const missing = ids.filter(id => id !== '*' && !names.value[id])
  for (let index = 0; index < missing.length; index += 100) {
    const chunk = missing.slice(index, index + 100)
    try {
      const params = new URLSearchParams({ code: props.permissionCode, ids: chunk.join(',') })
      const resolved = await api<DomainOption[]>(`/permissions/domains/resolve?${params}`)
      if (!active) return
      for (const domain of resolved) names.value[domain.id] = domain.name
    } catch {
      // 未解析的旧授权仍按 ID 展示，允许移除，不把名称猜成已授权域。
    }
  }
}

function applySearch() {
  search.value = searchDraft.value.trim()
  page.value = 1
  void loadPage()
}

function changePage(next: number) {
  if (next < 1 || next > pageCount.value || loading.value) return
  page.value = next
  void loadPage()
}

function toggleAll() {
  selected.value = allSelected.value ? [] : ['*']
}

function toggleDomain(id: string) {
  selected.value = allSelected.value
    ? [id]
    : specificIds.value.includes(id)
      ? specificIds.value.filter(value => value !== id)
      : [...specificIds.value, id]
}

watch(specificIds, ids => { void resolveSelected(ids) }, { immediate: true })
onMounted(() => { void loadPage() })
onBeforeUnmount(() => { active = false; version++ })
</script>

<template>
  <div class="domain-picker">
    <div class="domain-picker-search">
      <v-text-field v-model="searchDraft" label="搜索授权域" density="compact" hide-details clearable :disabled="disabled" @keyup.enter="applySearch" />
      <v-btn variant="outlined" :disabled="disabled || loading" @click="applySearch">搜索</v-btn>
    </div>
    <div class="domain-picker-selected">
      <span>已选域</span>
      <v-chip v-if="allSelected" size="small" closable :disabled="disabled" @click:close="toggleAll">所有域</v-chip>
      <v-chip v-for="id in specificIds" :key="id" size="small" closable :disabled="disabled" @click:close="toggleDomain(id)">
        {{ names[id] ?? `未知或无权域 · ${id}` }}
      </v-chip>
      <span v-if="!selected.length" class="text-medium-emphasis">尚未选择</span>
    </div>
    <div v-if="allSelected && !canSelectAll && !loading && !error" class="domain-picker-message" role="alert">当前账号不能授予所有域，请从下方选择可授权的域。</div>
    <div class="domain-picker-list">
      <v-checkbox v-if="canSelectAll" :model-value="allSelected" label="所有域" hide-details density="compact" :disabled="disabled" @update:model-value="toggleAll" />
      <v-progress-linear v-if="loading" indeterminate color="primary" />
      <div v-else-if="error" class="domain-picker-message" role="alert">{{ error }} <v-btn variant="text" size="small" @click="loadPage">重试</v-btn></div>
      <div v-else-if="!items.length" class="domain-picker-message">{{ search ? '没有符合条件的域' : '没有可选择的域' }}</div>
      <v-checkbox v-for="domain in items" v-else :key="domain.id" :model-value="specificIds.includes(domain.id)" :label="domain.name" hide-details density="compact" :disabled="disabled" @update:model-value="toggleDomain(domain.id)" />
    </div>
    <div v-if="!error" class="domain-picker-footer">
      <span>第 {{ page }} / {{ pageCount }} 页 · 共 {{ totalCount }} 个可选域</span>
      <div>
        <v-btn variant="text" size="small" :disabled="disabled || loading || page <= 1" @click="changePage(page - 1)">上一页</v-btn>
        <v-btn variant="text" size="small" :disabled="disabled || loading || page >= pageCount" @click="changePage(page + 1)">下一页</v-btn>
      </div>
    </div>
  </div>
</template>

<style scoped>
.domain-picker { display: grid; gap: 8px; max-width: 560px; margin: 2px 0 4px 38px; padding: 12px; border: 1px solid var(--border); border-radius: 8px; background: #fff; }
.domain-picker-search { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 8px; align-items: center; }
.domain-picker-selected { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; font-size: 12px; }
.domain-picker-selected > span:first-child { width: 100%; color: var(--text-secondary); }
.domain-picker-list { max-height: 240px; overflow: auto; border-top: 1px solid var(--border); padding-top: 6px; }
.domain-picker-message { padding: 10px 4px; color: var(--text-secondary); font-size: 12px; }
.domain-picker-footer { display: flex; justify-content: space-between; align-items: center; gap: 10px; color: var(--text-muted); font-size: 12px; }
@media (max-width: 720px) { .domain-picker { margin-left: 0; } }
</style>
