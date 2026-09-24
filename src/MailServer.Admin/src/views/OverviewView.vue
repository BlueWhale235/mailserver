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
  oldestPendingDeliveryAt: string | null
  generatedAt: string
}

interface TrafficBucket {
  from: string
  to: string
  storedMessages: number
  delivered: number
  temporaryFailures: number
  permanentFailures: number
}
interface Traffic { days: number; generatedAt: string; p95DeliveryMilliseconds: number | null; buckets: TrafficBucket[] }

const data = ref<Dashboard | null>(null)
const traffic = ref<Traffic | null>(null)
const trafficDays = ref<1 | 7>(1)
const loading = ref(false)
const trafficLoading = ref(false)
const error = ref('')
const trafficError = ref('')
let trafficRequest = 0
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
  await loadTraffic()
}

async function loadTraffic() {
  const request = ++trafficRequest
  trafficLoading.value = true
  trafficError.value = ''
  try {
    const result = await api<Traffic>(`/overview/traffic?days=${trafficDays.value}`)
    if (request === trafficRequest) traffic.value = result
  } catch (cause) {
    if (request === trafficRequest) {
      traffic.value = null
      trafficError.value = cause instanceof Error ? cause.message : '流量统计加载失败'
    }
  } finally {
    if (request === trafficRequest) trafficLoading.value = false
  }
}

function selectTrafficDays(days: 1 | 7) {
  if (trafficDays.value === days) return
  trafficDays.value = days
  void loadTraffic()
}

function dateTime(value: string) {
  return new Date(value).toLocaleString('zh-CN', { hour12: false })
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
  <v-alert v-else-if="data && data.directMxHealthyDomains > 0" class="readiness-alert" color="success" variant="tonal" icon="mdi-check-circle-outline">
    <b>Direct MX 域检查正常</b><span>{{ data.directMxHealthyDomains }} 个活动域已通过当前检查。</span>
  </v-alert>
  <v-alert v-else-if="data" class="readiness-alert" color="info" variant="tonal" icon="mdi-information-outline">
    <b>尚无已检查的活动域</b><span>添加并检查邮件域后，此处会显示 Direct MX 就绪状态。</span>
    <template #append><v-btn variant="text" to="/domains">查看域</v-btn></template>
  </v-alert>

  <section v-if="data" class="metric-grid overview-metrics">
    <article><span>管理域</span><strong>{{ data?.domains ?? '—' }}</strong><small>{{ data?.directMxHealthyDomains ?? 0 }} 个 Direct MX 已就绪</small></article>
    <article><span>邮箱账号</span><strong>{{ data?.accounts ?? '—' }}</strong><small>{{ data?.aliases ?? 0 }} 个别名</small></article>
    <article><span>待投递队列</span><strong>{{ data?.pendingDeliveries ?? '—' }}</strong><small :class="{ 'warning-text': (data?.pendingDeliveries ?? 0) > 0 }">{{ data?.oldestPendingDeliveryAt ? `最早入队 ${dateTime(data.oldestPendingDeliveryAt)}` : '等待或延迟投递' }}</small></article>
    <article><span>失败投递</span><strong>{{ data?.failedDeliveries ?? '—' }}</strong><small :class="{ 'warning-text': (data?.failedDeliveries ?? 0) > 0 }">其中 {{ data?.failedLast24Hours ?? 0 }} 封发生于 24 小时内</small></article>
    <article><span>24h 成功投递</span><strong>{{ data?.deliveredLast24Hours ?? '—' }}</strong><small>已完成的 Direct MX 任务</small></article>
    <article><span>邮件存储</span><strong class="metric-name">{{ data ? bytes(data.storedBytes) : '—' }}</strong><small>当前消息数据总量</small></article>
  </section>

  <v-card class="panel overview-traffic" flat>
    <div class="panel-head">
      <div><h2>真实邮件流量</h2><p>按入库时间与投递尝试时间统计；失败次数按尝试计算</p></div>
      <div class="traffic-controls">
        <v-btn size="small" :variant="trafficDays === 1 ? 'flat' : 'outlined'" :color="trafficDays === 1 ? 'primary' : undefined" @click="selectTrafficDays(1)">24 小时</v-btn>
        <v-btn size="small" :variant="trafficDays === 7 ? 'flat' : 'outlined'" :color="trafficDays === 7 ? 'primary' : undefined" @click="selectTrafficDays(7)">7 天</v-btn>
      </div>
    </div>
    <v-progress-linear v-if="trafficLoading" indeterminate color="primary" />
    <v-alert v-if="trafficError" type="error" variant="tonal" class="ma-4">{{ trafficError }} <v-btn variant="text" size="small" @click="loadTraffic">重试</v-btn></v-alert>
    <template v-else-if="traffic">
      <div class="traffic-summary">
        <span>成功投递 P95：{{ traffic.p95DeliveryMilliseconds == null ? '暂无数据' : `${traffic.p95DeliveryMilliseconds} 毫秒` }}</span>
        <span>更新于 {{ dateTime(traffic.generatedAt) }}</span>
      </div>
      <div class="traffic-table-wrap">
        <table class="traffic-table">
          <thead><tr><th>时段（本地时间）</th><th>新入库邮件</th><th>成功投递</th><th>临时失败</th><th>永久失败</th></tr></thead>
          <tbody>
            <tr v-for="bucket in traffic.buckets" :key="bucket.from">
              <td>{{ dateTime(bucket.from) }} 至 {{ dateTime(bucket.to) }}</td>
              <td>{{ bucket.storedMessages }}</td><td>{{ bucket.delivered }}</td>
              <td>{{ bucket.temporaryFailures }}</td><td>{{ bucket.permanentFailures }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </v-card>

  <section v-if="data" class="dashboard-grid">
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
        <div><span><v-icon size="18">mdi-email-alert-outline</v-icon>失败投递</span><v-btn variant="text" density="compact" to="/delivery?tab=bounces">{{ data?.failedDeliveries ?? '—' }}</v-btn></div>
        <div><span><v-icon size="18">mdi-tray-full</v-icon>待投递</span><v-btn variant="text" density="compact" to="/delivery?tab=queue">{{ data?.pendingDeliveries ?? '—' }}</v-btn></div>
      </div>
    </v-card>
  </section>
  <p v-if="data" class="overview-updated">概览数据更新于 {{ dateTime(data.generatedAt) }}</p>
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

.overview-traffic { margin-top: 20px; margin-bottom: 20px; }
.traffic-controls { display: flex; gap: 8px; }
.traffic-summary { display: flex; justify-content: space-between; gap: 16px; padding: 0 20px 14px; color: var(--color-text-muted); font-size: 13px; }
.traffic-table-wrap { overflow-x: auto; }
.traffic-table { width: 100%; border-collapse: collapse; font-size: 13px; }
.traffic-table th, .traffic-table td { padding: 11px 20px; text-align: left; border-top: 1px solid var(--color-border); white-space: nowrap; }
.traffic-table th { color: var(--color-text-muted); font-weight: 600; }
.overview-updated { margin-top: 16px; text-align: right; color: var(--color-text-muted); font-size: 12px; }

@media (max-width: 1100px) { .overview-metrics { grid-template-columns: 1fr 1fr; } }
</style>
