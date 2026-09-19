<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '../../lib/api'
import { isPreviewMode } from '../../lib/mockApi'
import StatusPill from '../../components/StatusPill.vue'
import type { DnsDiagnosticResult } from './types'

const props = defineProps<{ refreshKey: number }>()
const preview = isPreviewMode()
const domain = ref('gmail.com')
const selector = ref('mail')
const loading = ref(false)
const error = ref('')
const result = ref<DnsDiagnosticResult | null>(null)
const checkedAt = ref('')
const checkedSelector = ref('')

const nullMx = computed(() => result.value?.mx.some(record => /^0\s+\.?$/.test(record.trim())) ?? false)
const reachable = computed(() => result.value?.mxConnectivity.filter(item => item.success).length ?? 0)
const failedConnections = computed(() => result.value?.mxConnectivity.filter(item => !item.success).length ?? 0)
const stageRows = computed(() => {
  const current = result.value
  if (!current) return []
  return [
    {
      key: 'dns', icon: 'mdi-dns-outline', title: 'DNS 路由',
      status: nullMx.value ? 'danger' : !current.mx.length ? 'warning' : 'success',
      label: nullMx.value ? 'Null MX' : current.mx.length ? `${current.mx.length} 个 MX` : '未找到 MX',
      description: nullMx.value ? '收件域明确声明不接收邮件，投递应永久失败。' : current.mx.join(' · ') || '真实投递会继续尝试 A/AAAA 回退。',
    },
    {
      key: 'connect', icon: 'mdi-lan-connect', title: '25 端口连通性',
      status: reachable.value ? (failedConnections.value ? 'warning' : 'success') : current.mxConnectivity.length ? 'danger' : 'neutral',
      label: reachable.value ? `${reachable.value} 条 TCP 路径可达` : current.mxConnectivity.length ? '探测连接失败' : '未探测',
      description: current.mxConnectivity.length ? `${current.mxConnectivity.length} 个 IP 已探测，${failedConnections.value} 个失败；实际投递会继续尝试其他 MX/IP。` : '没有可探测的 MX 地址。',
    },
    {
      key: 'tls', icon: 'mdi-shield-lock-outline', title: '传输安全策略',
      status: 'warning',
      label: current.mtaSts.length ? '发现 MTA-STS 记录' : current.tlsa.length ? '发现 TLSA 记录' : '未发现强制策略记录',
      description: current.mtaSts.length ? current.mtaSts.join(' · ') : current.tlsa.length ? `${current.tlsa.length} 条 TLSA 记录` : '未发现强制策略；支持时仍会发起 STARTTLS。',
    },
    {
      key: 'identity', icon: 'mdi-domain', title: '域身份记录',
      status: current.spf.length && current.dmarc.length ? 'success' : 'warning',
      label: `${current.spf.length + current.dmarc.length + current.dkim.length} 条策略`,
      description: `SPF ${current.spf.length ? '已发布' : '缺失'} · DMARC ${current.dmarc.length ? '已发布' : '缺失'} · DKIM ${checkedSelector.value ? current.dkim.length ? '已找到' : '未找到' : '未指定选择器'}`,
    },
    {
      key: 'rpt', icon: 'mdi-file-chart-outline', title: 'TLS 报告',
      status: current.tlsRpt.length ? 'success' : 'neutral',
      label: current.tlsRpt.length ? '已配置 TLS-RPT' : '未配置',
      description: current.tlsRpt.join(' · ') || '不会影响正常 Direct MX 投递，但无法接收聚合 TLS 失败报告。',
    },
  ] as const
})

function tone(value: string) {
  return value === 'success' ? 'success' : value === 'danger' ? 'danger' : value === 'warning' ? 'warning' : 'neutral'
}

async function diagnose() {
  if (loading.value) return
  const normalized = domain.value.trim().toLowerCase().replace(/\.$/, '')
  if (!/^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(normalized)) {
    error.value = '请输入有效的收件域名，例如 gmail.com'
    return
  }
  loading.value = true
  result.value = null
  error.value = ''
  try {
    const query = new globalThis.URLSearchParams({ domain: normalized })
    if (selector.value.trim()) query.set('selector', selector.value.trim())
    result.value = await api<DnsDiagnosticResult>(`/diagnostics/dns?${query}`)
    checkedSelector.value = selector.value.trim()
    checkedAt.value = new Date().toISOString()
  } catch (cause) {
    result.value = null
    error.value = cause instanceof Error ? cause.message : 'Direct MX 诊断失败'
  } finally { loading.value = false }
}

function useExample(value: string) {
  if (loading.value) return
  domain.value = value
  void diagnose()
}
watch(() => props.refreshKey, () => { if (result.value) void diagnose() })
</script>

<template>
  <section class="diagnostic-layout">
    <v-card flat class="panel diagnostic-form-panel">
      <div class="panel-head"><div><h2>新建诊断</h2><p>检查收件域 DNS 记录与端口 25 的 TCP 连通性。</p></div></div>
      <v-form class="diagnostic-form" @submit.prevent="diagnose">
        <v-text-field v-model="domain" label="收件域名" placeholder="例如 gmail.com" prepend-inner-icon="mdi-web" :disabled="loading" autocomplete="off" />
        <v-text-field v-model="selector" label="DKIM 选择器（可选）" placeholder="例如 mail" :disabled="loading" autocomplete="off" />
        <v-btn type="submit" color="primary" size="large" block prepend-icon="mdi-stethoscope" :loading="loading">开始诊断</v-btn>
      </v-form>
      <div v-if="preview" class="diagnostic-examples">
        <span>演示场景</span>
        <button type="button" @click="useExample('gmail.com')">正常域</button>
        <button type="button" @click="useExample('mail.secure.example')">DANE 域</button>
        <button type="button" @click="useExample('blocked.invalid')">Null MX</button>
      </div>
      <v-alert v-if="error" type="error" variant="tonal" class="mx-5 mb-5" closable @click:close="error = ''">{{ error }}</v-alert>
      <div class="diagnostic-note"><v-icon size="18">mdi-information-outline</v-icon><p><b>诊断不会阻止外发</b><span>结果只反映当前节点视角；公网 25、PTR 与 IP 信誉仍需管理员确认。</span></p></div>
    </v-card>

    <div class="diagnostic-result-column">
      <v-card v-if="!result && !loading" flat class="panel diagnostic-welcome">
        <div class="route-visual"><span>DNS</span><i></i><span>MX</span><i></i><span>TLS</span><i></i><span>SMTP</span></div>
        <h2>按投递顺序查看问题</h2>
        <p>查看 DNS 路由、TCP 连通性和安全策略记录。本检查不执行 TLS 握手或 SMTP 投递，实际响应请在投递尝试中查看。</p>
      </v-card>
      <v-card v-else flat class="panel diagnostic-results">
        <header class="diagnostic-result-head">
          <div><h2>{{ result?.domain || domain }}</h2><p v-if="checkedAt">检查于 {{ new Date(checkedAt).toLocaleString('zh-CN', { hour12: false }) }}</p></div>
          <StatusPill v-if="result" :label="nullMx ? 'Null MX · 不接收邮件' : reachable ? 'TCP 可达 · 投递尚未验证' : '连通性需确认'" :tone="nullMx ? 'danger' : 'warning'" />
        </header>
        <v-progress-linear v-if="loading" indeterminate color="primary" />
        <template v-if="result">
          <v-alert type="info" variant="tonal" class="mx-5 mt-4">TCP 可达或存在安全记录不代表投递成功；未验证 TLS 握手、DNSSEC、完整策略及 SMTP 接收结果。</v-alert>
          <div class="diagnostic-stages">
            <article v-for="stage in stageRows" :key="stage.key">
              <div class="stage-icon"><v-icon>{{ stage.icon }}</v-icon></div>
              <div><h3>{{ stage.title }}</h3><p>{{ stage.description }}</p></div>
              <StatusPill :label="stage.label" :tone="tone(stage.status)" />
            </article>
          </div>

          <div v-if="result.warnings.length" class="diagnostic-warnings">
            <h3><v-icon size="18">mdi-alert-outline</v-icon> 需要关注</h3>
            <ul><li v-for="warning in result.warnings" :key="warning">{{ warning }}</li></ul>
          </div>

          <div class="connectivity-section">
            <div><h3>MX / IP 故障转移路径</h3><p>同优先级 MX 在真实投递时会随机化；单一路径失败不会立即终止。</p></div>
            <v-table density="compact">
              <thead><tr><th>MX 主机</th><th>地址</th><th>协议</th><th>端口</th><th>探测结果</th></tr></thead>
              <tbody>
                <tr v-for="item in result.mxConnectivity" :key="`${item.host}-${item.address}`"><td>{{ item.host }}</td><td><code>{{ item.address }}</code></td><td>{{ item.addressFamily }}</td><td>{{ item.port }}</td><td><StatusPill :label="item.success ? '可连接' : item.error || '失败'" :tone="item.success ? 'success' : 'danger'" /></td></tr>
                <tr v-if="!result.mxConnectivity.length"><td colspan="5" class="text-center text-medium-emphasis py-6">没有可探测的 MX 地址</td></tr>
              </tbody>
            </v-table>
          </div>
        </template>
      </v-card>
    </div>
  </section>
</template>

<style scoped>
.diagnostic-layout { display: grid; grid-template-columns: 360px minmax(0, 1fr); gap: 16px; align-items: start; }
.diagnostic-form-panel { position: sticky; top: 86px; }
.diagnostic-form { display: grid; gap: 20px; padding: 10px 20px 18px; }
.diagnostic-examples { display: flex; align-items: center; flex-wrap: wrap; gap: 7px; padding: 0 20px 18px; color: var(--text-muted); font-size: 11px; }
.diagnostic-examples button { border: 0; border-radius: 6px; padding: 5px 8px; background: var(--bg-app); color: var(--color-primary); cursor: pointer; font: inherit; }
.diagnostic-examples button:hover { background: var(--color-primary-soft); }
.diagnostic-note { display: flex; align-items: flex-start; gap: 10px; margin: 0 20px 20px; padding: 13px; border-radius: 8px; background: #f2f6fc; color: #315c96; }
.diagnostic-note p { margin: 0; }
.diagnostic-note b, .diagnostic-note span { display: block; }
.diagnostic-note b { font-size: 12px; }
.diagnostic-note span { margin-top: 3px; color: #647c9e; font-size: 11px; }
.diagnostic-welcome { min-height: 520px; display: flex; flex-direction: column; align-items: center; justify-content: center; padding: 48px; text-align: center; }
.diagnostic-welcome h2 { margin: 26px 0 8px; font-size: 20px; }
.diagnostic-welcome p { max-width: 540px; margin: 0; color: var(--text-secondary); font-size: 13px; line-height: 1.7; }
.route-visual { display: flex; align-items: center; gap: 10px; }
.route-visual span { width: 58px; height: 58px; display: grid; place-items: center; border: 1px solid #cbd9ef; border-radius: 50%; background: #f7faff; color: #315f9f; font-size: 12px; font-weight: 700; }
.route-visual i { width: 38px; height: 1px; background: #b9c7dc; position: relative; }
.route-visual i::after { content: ''; position: absolute; right: 0; top: -3px; width: 6px; height: 6px; border-top: 1px solid #879ab8; border-right: 1px solid #879ab8; transform: rotate(45deg); }
.diagnostic-result-head { display: flex; align-items: center; justify-content: space-between; gap: 20px; padding: 20px 22px 16px; border-bottom: 1px solid var(--border); }
.diagnostic-result-head h2 { margin: 0; font-size: 17px; }
.diagnostic-result-head p { margin: 4px 0 0; color: var(--text-muted); font-size: 11px; }
.diagnostic-stages { padding: 0 22px; }
.diagnostic-stages article { display: grid; grid-template-columns: 38px minmax(0, 1fr) auto; align-items: center; gap: 13px; padding: 17px 0; border-bottom: 1px solid var(--border); }
.stage-icon { width: 36px; height: 36px; display: grid; place-items: center; border-radius: 8px; background: var(--color-primary-soft); color: var(--color-primary); }
.diagnostic-stages h3, .diagnostic-stages p { margin: 0; }
.diagnostic-stages h3 { font-size: 13px; }
.diagnostic-stages p { margin-top: 4px; color: var(--text-secondary); font-size: 11px; overflow-wrap: anywhere; }
.diagnostic-warnings { margin: 18px 22px; padding: 14px 16px; border-left: 3px solid #e7a524; background: #fff8e9; }
.diagnostic-warnings h3 { display: flex; align-items: center; gap: 7px; margin: 0 0 8px; color: #8c5d08; font-size: 12px; }
.diagnostic-warnings ul { margin: 0; padding-left: 18px; color: #7d6332; font-size: 11px; line-height: 1.7; }
.connectivity-section { padding: 2px 22px 22px; }
.connectivity-section > div { margin-bottom: 10px; }
.connectivity-section h3, .connectivity-section p { margin: 0; }
.connectivity-section h3 { font-size: 13px; }
.connectivity-section p { margin-top: 4px; color: var(--text-muted); font-size: 11px; }
.connectivity-section .v-table { border: 1px solid var(--border); border-radius: 8px; }
.connectivity-section code { font-size: 11px; }
@media (max-width: 1100px) {
  .diagnostic-layout { grid-template-columns: 1fr; }
  .diagnostic-form-panel { position: static; }
}
</style>
