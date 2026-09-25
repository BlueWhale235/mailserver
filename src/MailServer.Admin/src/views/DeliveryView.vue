<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '../lib/api'
import PageHeader from '../components/PageHeader.vue'
import WorkspaceTabs from '../components/WorkspaceTabs.vue'
import QueueWorkspace from './delivery/QueueWorkspace.vue'
import DirectMxDiagnostic from './delivery/DirectMxDiagnostic.vue'
import TlsReportsWorkspace from './delivery/TlsReportsWorkspace.vue'
import type { DeliverySummary, DeliveryWorkspace } from './delivery/types'

type DeliveryTab = DeliveryWorkspace | 'diagnostic' | 'tls-reports'

const tabKeys = ['queue', 'history', 'bounces', 'diagnostic', 'tls-reports'] as const
function normalizeTab(value: unknown): DeliveryTab {
  const candidate = String(value ?? '')
  return (tabKeys as readonly string[]).includes(candidate) ? candidate as DeliveryTab : 'queue'
}

const route = useRoute()
const router = useRouter()
const tab = ref<DeliveryTab>(normalizeTab(route.query.tab))
const summary = ref<DeliverySummary | null>(null)
const summaryLoading = ref(false)
const error = ref('')
const refreshKey = ref(0)

const activeQueueCount = computed(() => summary.value
  ? summary.value.pending + summary.value.processing + summary.value.deferred + summary.value.failed
  : 0)
const tabItems = computed(() => [
  { value: 'queue', label: '队列', count: activeQueueCount.value },
  { value: 'history', label: '投递历史', count: summary.value?.deliveredLast24Hours ?? 0, hint: '24h' },
  { value: 'bounces', label: '退信', count: summary.value?.failed ?? 0 },
  { value: 'diagnostic', label: 'Direct MX 诊断' },
  { value: 'tls-reports', label: 'TLS 报告' },
])

async function loadSummary() {
  summaryLoading.value = true
  error.value = ''
  try { summary.value = await api<DeliverySummary>('/queues/summary') }
  catch (cause) { error.value = cause instanceof Error ? cause.message : '投递摘要加载失败' }
  finally { summaryLoading.value = false }
}

async function refreshCurrent() {
  refreshKey.value += 1
  await loadSummary()
}

function changed() { void loadSummary() }

// 工作区写入 URL（?tab=），便于定位、刷新与分享具体视图，与域详情三级菜单保持一致。
watch(tab, value => {
  if (String(route.query.tab ?? '') === value) return
  void router.replace({ query: { ...route.query, tab: value } })
}, { immediate: true })

// 允许直接通过 URL 进入某个工作区。
watch(() => route.query.tab, value => {
  const next = normalizeTab(value)
  if (next !== tab.value) tab.value = next
})

onMounted(loadSummary)
</script>

<template>
  <PageHeader title="投递中心" description="监控 Direct MX 队列、远端响应、退信和 TLS 报告">
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="summaryLoading" @click="refreshCurrent">刷新当前视图</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>

  <section class="delivery-summary delivery-command-summary">
    <div><span class="icon-blue"><v-icon>mdi-tray-full</v-icon></span><p>等待处理<strong>{{ summary ? summary.pending + summary.processing : '—' }}</strong><small>{{ summary?.processing ?? 0 }} 封正在投递</small></p></div>
    <div><span class="icon-amber"><v-icon>mdi-clock-alert-outline</v-icon></span><p>延迟重试<strong>{{ summary?.deferred ?? '—' }}</strong><small>按退避计划自动重试</small></p></div>
    <div><span class="icon-red"><v-icon>mdi-email-remove-outline</v-icon></span><p>永久失败<strong>{{ summary?.failed ?? '—' }}</strong><small>{{ summary?.failedLast24Hours ?? 0 }} 封发生于 24h 内</small></p></div>
    <div><span class="icon-green"><v-icon>mdi-check-decagram-outline</v-icon></span><p>24h 已投递<strong>{{ summary?.deliveredLast24Hours ?? '—' }}</strong><small>平均尝试 {{ summary ? `${Math.round(summary.averageAttemptMillisecondsLast24Hours)} ms` : '—' }}</small></p></div>
  </section>

  <WorkspaceTabs :model-value="tab" :items="tabItems" label="投递中心工作区" @update:model-value="value => tab = value as DeliveryTab" />

  <QueueWorkspace v-if="tab === 'queue' || tab === 'history' || tab === 'bounces'" :key="tab" :mode="tab" :refresh-key="refreshKey" @changed="changed" />
  <DirectMxDiagnostic v-else-if="tab === 'diagnostic'" :refresh-key="refreshKey" />
  <TlsReportsWorkspace v-else :refresh-key="refreshKey" />
</template>

<style scoped>
.delivery-command-summary { margin-bottom: 18px; }
</style>
