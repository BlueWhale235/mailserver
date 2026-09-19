<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '../lib/api'
import PageHeader from '../components/PageHeader.vue'
import StatusPill from '../components/StatusPill.vue'

interface Dashboard {
  domains: number
  accounts: number
  aliases: number
  pendingDeliveries: number
  failedDeliveries: number
  storedBytes: number
  directMxHealthyDomains: number
  directMxAttentionDomains: number
  directMxUncheckedDomains: number
  expiringCertificates: number
  deliveredLast24Hours: number
  failedLast24Hours: number
}

const data = ref<Dashboard | null>(null)
const loading = ref(false)
const error = ref('')
const readinessAttention = computed(() =>
  (data.value?.directMxAttentionDomains ?? 0) + (data.value?.directMxUncheckedDomains ?? 0))

async function loadOverview() {
  loading.value = true
  error.value = ''
  try {
    data.value = await api<Dashboard>('/overview')
  } catch (cause) {
    data.value = null
    error.value = cause instanceof Error ? cause.message : '系统概览加载失败'
  } finally {
    loading.value = false
  }
}

function bytes(value: number) {
  if (!value) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1)
  return `${(value / 1024 ** index).toFixed(index > 2 ? 1 : 0)} ${units[index]}`
}

onMounted(loadOverview)
</script>

<template>
  <PageHeader title="系统概览" description="来自当前服务器的邮件流、队列和 Direct MX 就绪状态">
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="loading" @click="loadOverview">刷新</v-btn>
    <v-btn color="primary" prepend-icon="mdi-stethoscope" to="/domains">检查邮件域</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" class="mb-4">
    <b>无法读取服务器状态</b><span>{{ error }}</span>
  </v-alert>
  <v-alert v-else-if="data && readinessAttention" class="readiness-alert" color="warning" variant="tonal" icon="mdi-alert-outline">
    <b>Direct MX 就绪性需要关注</b>
    <span>{{ data.directMxAttentionDomains }} 个域检查失败，{{ data.directMxUncheckedDomains }} 个域尚未完成检查。告警不会阻止外发。</span>
    <template #append><v-btn variant="text" to="/domains">查看域</v-btn></template>
  </v-alert>
  <v-alert v-else-if="data" class="readiness-alert" color="success" variant="tonal" icon="mdi-check-circle-outline">
    <b>Direct MX 域检查正常</b><span>{{ data.directMxHealthyDomains }} 个活动域已通过当前检查。</span>
  </v-alert>

  <section class="metric-grid overview-metrics">
    <article><span>管理域</span><strong>{{ data?.domains ?? '—' }}</strong><small>{{ data?.directMxHealthyDomains ?? 0 }} 个 Direct MX 已就绪</small></article>
    <article><span>邮箱账号</span><strong>{{ data?.accounts ?? '—' }}</strong><small>{{ data?.aliases ?? 0 }} 个别名</small></article>
    <article><span>待投递队列</span><strong>{{ data?.pendingDeliveries ?? '—' }}</strong><small :class="{ 'warning-text': (data?.pendingDeliveries ?? 0) > 0 }">等待或延迟投递</small></article>
    <article><span>失败投递</span><strong>{{ data?.failedDeliveries ?? '—' }}</strong><small :class="{ 'warning-text': (data?.failedDeliveries ?? 0) > 0 }">其中 {{ data?.failedLast24Hours ?? 0 }} 封发生于 24 小时内</small></article>
    <article><span>24h 成功投递</span><strong>{{ data?.deliveredLast24Hours ?? '—' }}</strong><small>已完成的 Direct MX 任务</small></article>
    <article><span>邮件存储</span><strong class="metric-name">{{ data ? bytes(data.storedBytes) : '—' }}</strong><small>当前消息数据总量</small></article>
  </section>

  <section class="dashboard-grid">
    <v-card class="panel" flat>
      <div class="panel-head"><div><h2>Direct MX 就绪性</h2><p>活动域的最新 DNS 与投递前置检查</p></div><v-btn variant="text" to="/domains">管理域</v-btn></div>
      <div class="health-list">
        <div><span><v-icon size="18" color="success">mdi-check-circle</v-icon>已就绪</span><b>{{ data?.directMxHealthyDomains ?? 0 }}</b></div>
        <div><span><v-icon size="18" color="warning">mdi-alert-circle</v-icon>需关注</span><b class="warning-text">{{ data?.directMxAttentionDomains ?? 0 }}</b></div>
        <div><span><v-icon size="18" color="grey">mdi-help-circle</v-icon>未检查</span><b>{{ data?.directMxUncheckedDomains ?? 0 }}</b></div>
      </div>
    </v-card>
    <v-card class="panel" flat>
      <div class="panel-head"><div><h2>需要处理</h2><p>由当前数据库实时计算</p></div><StatusPill :label="readinessAttention || (data?.expiringCertificates ?? 0) ? '需关注' : '正常'" :tone="readinessAttention || (data?.expiringCertificates ?? 0) ? 'warning' : 'success'" /></div>
      <div class="health-list">
        <div><span><v-icon size="18">mdi-certificate-outline</v-icon>30 天内到期证书</span><b>{{ data?.expiringCertificates ?? 0 }}</b></div>
        <div><span><v-icon size="18">mdi-email-alert-outline</v-icon>失败投递</span><b>{{ data?.failedDeliveries ?? 0 }}</b></div>
        <div><span><v-icon size="18">mdi-tray-full</v-icon>待投递</span><b>{{ data?.pendingDeliveries ?? 0 }}</b></div>
      </div>
    </v-card>
  </section>
</template>

<style scoped>
.overview-metrics { grid-template-columns: repeat(3, 1fr); }

.overview-metrics article {
  position: relative;
  overflow: hidden;
}

.overview-metrics article::before {
  content: "";
  position: absolute;
  inset: 0 auto 0 0;
  width: 3px;
  border-radius: 0 3px 3px 0;
  background: var(--color-primary-soft);
  transition: background 0.15s ease;
}

.overview-metrics article:hover::before { background: var(--color-primary); }

@media (max-width: 1100px) { .overview-metrics { grid-template-columns: 1fr 1fr; } }
</style>
