<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import StatusPill from './StatusPill.vue'
import {
  alignmentOptions,
  buildSpfValue,
  dmarcPolicyOptions,
  dmarcSubdomainPolicyOptions,
  fetchDomainDns,
  generateDomainDkim,
  mtaStsModeOptions,
  saveDomainDns,
  spfAllPolicyOptions,
  splitList,
  type DomainDnsCheck,
  type DomainDnsConfiguration,
  type DomainDnsSettingsModel,
} from '../lib/domainDns'

const route = useRoute()
const router = useRouter()

const props = defineProps<{
  domainId: string
  domainName: string
  mailHostName: string
  canManage: boolean
  dkimSelector?: string
  dkimPublicKey?: string
  dnsCheck?: DomainDnsCheck | null
  dnsLoading?: boolean
}>()

const emit = defineEmits<{
  saved: [config: DomainDnsConfiguration]
  dkimGenerated: [payload: { selector: string; publicKey: string }]
  check: []
  validate: []
}>()

type SectionKey = 'check' | 'spf' | 'dmarc' | 'mta' | 'dane' | 'dkim' | 'records'

const sectionKeys: SectionKey[] = ['check', 'spf', 'dmarc', 'mta', 'dane', 'dkim', 'records']

function normalizeSection(value: unknown): SectionKey {
  return sectionKeys.includes(value as SectionKey) ? (value as SectionKey) : 'check'
}

const sections: Array<{ key: SectionKey; title: string; description: string; icon: string }> = [
  { key: 'check', title: '发布检查', description: '核对已发布的 DNS 记录是否生效', icon: 'mdi-magnify-scan' },
  { key: 'spf', title: 'SPF 发信来源', description: '声明哪些主机可以用本域发信', icon: 'mdi-email-outline' },
  { key: 'dmarc', title: 'DMARC 对齐与报告', description: '处理未通过 SPF/DKIM 校验的邮件', icon: 'mdi-shield-account-outline' },
  { key: 'mta', title: 'MTA-STS 与 TLS 报告', description: '强制入站 SMTP 使用 TLS 并接收报告', icon: 'mdi-lock-check-outline' },
  { key: 'dane', title: 'DANE / TLSA', description: '通过 DNSSEC 绑定邮件主机证书', icon: 'mdi-certificate-outline' },
  { key: 'dkim', title: 'DKIM 签名密钥', description: '生成 RSA 2048 密钥并发布公钥', icon: 'mdi-key-outline' },
  { key: 'records', title: '待发布 DNS 记录', description: '汇总需要添加到 DNS 的记录', icon: 'mdi-format-list-bulleted' },
]

const activeSection = ref<SectionKey>(normalizeSection(route.params.section))
watch(() => route.params.section, value => {
  const next = normalizeSection(value)
  if (next !== activeSection.value) activeSection.value = next
})
watch(activeSection, value => {
  const query = { ...route.query }
  delete query.tab
  delete query.section
  const clean = !('tab' in route.query) && !('section' in route.query)
  if (route.params.tab === 'dns' && String(route.params.section ?? '') === value && clean) return
  void router.replace({ name: 'domain-detail', params: { id: props.domainId, tab: 'dns', section: value }, query })
}, { immediate: true })
const currentSection = computed(() => sections.find(section => section.key === activeSection.value)!)
const loading = ref(false)
const saving = ref(false)
const generatingDkim = ref(false)
const error = ref('')
const notice = ref('')
const settings = ref<DomainDnsSettingsModel | null>(null)
const records = ref<DomainDnsConfiguration['records']>([])
const loadedSignature = ref('')
const dkimSelectorInput = ref('')
const lastGeneratedKey = ref('')
const copiedName = ref('')

const checkItems = computed(() => props.dnsCheck?.validation?.items ?? [])
const checkWarnings = computed(() => props.dnsCheck?.validation?.warnings ?? [])
const checkAttention = computed(() => checkItems.value.filter(item => item.status.toLowerCase() !== 'match').length + checkWarnings.value.length)
const checkStatus = computed(() => {
  const status = props.dnsCheck?.status
  if (status === 'Healthy') return { label: checkAttention.value ? '已就绪，有提示' : '已就绪', tone: checkAttention.value ? 'warning' as const : 'success' as const }
  if (status === 'Unhealthy') return { label: '需要处理', tone: 'danger' as const }
  if (status === 'Checking') return { label: '检查中', tone: 'neutral' as const }
  return { label: '尚未检查', tone: 'neutral' as const }
})

function formatCheckTime(value?: string) {
  return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
}

const dirty = computed(() => Boolean(settings.value) && JSON.stringify(settings.value) !== loadedSignature.value)
const spfPreview = computed(() => settings.value ? buildSpfValue(settings.value) : '')
const dmarcPreview = computed(() => {
  const value = settings.value
  if (!value) return ''
  const parts = ['v=DMARC1', `p=${value.dmarcPolicy}`]
  if (value.dmarcSubdomainPolicy) parts.push(`sp=${value.dmarcSubdomainPolicy}`)
  parts.push(`pct=${value.dmarcPercentage}`)
  if (value.dmarcAggregateReportUris.trim()) parts.push(`rua=${splitList(value.dmarcAggregateReportUris).join(',')}`)
  if (value.dmarcForensicReportUris.trim()) parts.push(`ruf=${splitList(value.dmarcForensicReportUris).join(',')}`)
  parts.push(`adkim=${value.dmarcDkimAlignment}`, `aspf=${value.dmarcSpfAlignment}`, `fo=${value.dmarcFailureOptions || '0'}`)
  return parts.join('; ')
})
const mtaStsPreview = computed(() => settings.value && settings.value.mtaStsMode !== 'none'
  ? `_mta-sts.${props.domainName} TXT "v=STSv1; id=${settings.value.mtaStsPolicyId};"`
  : '')
const tlsRptPreview = computed(() => settings.value && settings.value.tlsReportUris.trim()
  ? `_smtp._tls.${props.domainName} TXT "v=TLSRPTv1; rua=${splitList(settings.value.tlsReportUris).join(',')}"`
  : '')
const dkimRecord = computed(() => {
  const key = lastGeneratedKey.value || props.dkimPublicKey || ''
  const selector = dkimSelectorInput.value.trim() || props.dkimSelector || ''
  if (!key || !selector) return ''
  return `${selector}._domainkey.${props.domainName} TXT "v=DKIM1; k=rsa; p=${key}"`
})

const formError = computed(() => {
  const value = settings.value
  if (!value) return ''
  if (!['-all', '~all', '?all', '+all'].includes(value.spfAllPolicy)) return 'SPF all 策略无效。'
  if (!['none', 'quarantine', 'reject'].includes(value.dmarcPolicy)) return 'DMARC 策略无效。'
  if (value.dmarcSubdomainPolicy && !['none', 'quarantine', 'reject'].includes(value.dmarcSubdomainPolicy)) return 'DMARC 子域策略无效。'
  if (!Number.isInteger(value.dmarcPercentage) || value.dmarcPercentage < 0 || value.dmarcPercentage > 100) return 'DMARC 百分比必须是 0 到 100 的整数。'
  if (!/^[A-Za-z0-9]{1,32}$/.test(value.mtaStsPolicyId)) return 'MTA-STS 策略 ID 只能是 1-32 位字母或数字。'
  if (!Number.isInteger(value.mtaStsMaxAgeSeconds) || value.mtaStsMaxAgeSeconds < 0 || value.mtaStsMaxAgeSeconds > 31_557_600) return 'MTA-STS max_age 必须在 0 到 31557600 秒之间。'
  if (value.mtaStsMode !== 'none' && splitList(value.mtaStsMxPatterns).length === 0) return '启用 MTA-STS 后必须填写至少一个 MX 模式。'
  if (value.daneEnabled && splitList(value.daneTlsaRecords).length === 0) return '启用 DANE 后必须填写至少一条 TLSA 记录。'
  return ''
})
const canSave = computed(() => props.canManage && !saving.value && !loading.value && Boolean(settings.value) && !formError.value)

async function load() {
  loading.value = true
  error.value = ''
  try {
    const config = await fetchDomainDns(props.domainId)
    settings.value = { ...config.settings }
    records.value = config.records
    loadedSignature.value = JSON.stringify(settings.value)
    dkimSelectorInput.value = props.dkimSelector || `s${new Date().toISOString().slice(0, 7).replace('-', '')}`
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'DNS 配置加载失败'
  } finally {
    loading.value = false
  }
}

function applyDirectMxPreset() {
  const value = settings.value
  if (!value) return
  value.spfIncludeMx = true
  value.spfIncludeA = false
  value.spfIncludes = ''
  value.spfAllPolicy = '-all'
  notice.value = '已应用 Direct MX 预设：仅允许本域 MX 主机发信。'
}

async function save() {
  if (!canSave.value || !settings.value) return
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    const config = await saveDomainDns(props.domainId, settings.value)
    settings.value = { ...config.settings }
    records.value = config.records
    loadedSignature.value = JSON.stringify(settings.value)
    notice.value = 'DNS 安全策略已保存，下方记录已重新生成。'
    emit('saved', config)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'DNS 配置保存失败'
  } finally {
    saving.value = false
  }
}

async function generateDkim() {
  const selector = dkimSelectorInput.value.trim().toLowerCase()
  if (!props.canManage || generatingDkim.value) return
  if (!/^[a-z0-9-]{1,63}$/.test(selector)) {
    error.value = 'DKIM selector 只能包含字母、数字和连字符，长度 1-63。'
    return
  }
  generatingDkim.value = true
  error.value = ''
  notice.value = ''
  try {
    const result = await generateDomainDkim(props.domainId, selector)
    lastGeneratedKey.value = result.publicKey
    dkimSelectorInput.value = selector
    notice.value = `DKIM 密钥已生成，请发布 selector「${selector}」对应的 TXT 记录。`
    emit('dkimGenerated', { selector, publicKey: result.publicKey })
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'DKIM 密钥生成失败'
  } finally {
    generatingDkim.value = false
  }
}

async function copyRecord(record: { name: string; type: string; value: string }) {
  const text = record.type === 'TXT' ? `${record.name} TXT "${record.value}"` : `${record.name} ${record.type} ${record.value}`
  try {
    await navigator.clipboard.writeText(text)
    copiedName.value = record.name
    globalThis.setTimeout(() => { if (copiedName.value === record.name) copiedName.value = '' }, 2000)
  } catch {
    error.value = '浏览器未授予剪贴板权限，请手动复制记录值。'
  }
}

async function copyAll() {
  const lines = records.value.map(record => record.zoneFileValue || `${record.name} ${record.type} ${record.value}`)
  try {
    await navigator.clipboard.writeText(lines.join('\n'))
    copiedName.value = '__all__'
    globalThis.setTimeout(() => { if (copiedName.value === '__all__') copiedName.value = '' }, 2000)
  } catch {
    error.value = '浏览器未授予剪贴板权限，请手动复制记录值。'
  }
}

watch(() => props.domainId, () => void load())
onMounted(load)
</script>

<template>
  <section class="dns-settings">
    <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>
    <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>
    <v-alert v-if="!canManage" type="info" variant="tonal" class="mb-4">当前账号只有只读权限，可以查看策略与记录，但不能修改。</v-alert>

    <v-progress-linear v-if="loading && !settings" indeterminate color="primary" class="mb-4" />

    <div v-if="settings" class="dns-workspace">
      <nav class="dns-nav" aria-label="DNS 与安全分区">
        <button v-for="section in sections" :key="section.key" :class="{ active: activeSection === section.key }" @click="activeSection = section.key">
          <v-icon size="20">{{ section.icon }}</v-icon>
          <span><b>{{ section.title }}</b><small>{{ section.description }}</small></span>
          <v-icon v-if="section.key === 'check' && dnsCheck?.status === 'Healthy'" size="16" :color="checkAttention ? 'warning' : 'success'">{{ checkAttention ? 'mdi-alert-circle-outline' : 'mdi-check-circle-outline' }}</v-icon>
          <i v-else-if="section.key === 'spf' && dirty" class="status-dot is-dirty" title="有未保存修改" />
          <v-icon v-else-if="section.key === 'dkim' && (dkimPublicKey || lastGeneratedKey)" size="16" color="success">mdi-check-circle-outline</v-icon>
          <i v-else />
        </button>
        <div class="nav-note"><v-icon size="17">mdi-shield-lock-outline</v-icon><span>保存策略后由服务器生成 SPF、DMARC、MTA-STS、TLS-RPT 与 TLSA 记录。</span></div>
      </nav>

      <v-card flat class="panel dns-panel">
        <div class="section-header">
          <div>
            <span>DNS 与安全</span>
            <h2>{{ currentSection.title }}</h2>
            <p>{{ currentSection.description }}</p>
          </div>
          <div class="head-actions">
            <StatusPill v-if="dirty" label="有未保存修改" tone="warning" />
            <StatusPill v-if="activeSection === 'dkim'" :label="dkimPublicKey || lastGeneratedKey ? '已生成' : '未生成'" :tone="dkimPublicKey || lastGeneratedKey ? 'success' : 'warning'" />
            <StatusPill v-if="activeSection === 'records'" :label="`共 ${records.length} 条`" tone="neutral" />
            <template v-if="activeSection === 'check'">
              <StatusPill v-bind="checkStatus" />
              <span class="last-check">最近检查 {{ formatCheckTime(dnsCheck?.checkedAt) }}</span>
              <v-btn variant="outlined" size="small" prepend-icon="mdi-magnify-scan" :loading="dnsLoading" @click="emit('validate')">校验发布</v-btn>
              <v-btn color="primary" size="small" prepend-icon="mdi-refresh" :loading="dnsLoading" @click="emit('check')">重新检查</v-btn>
            </template>
            <template v-if="activeSection === 'spf' && canManage">
              <v-btn variant="tonal" size="small" prepend-icon="mdi-auto-fix" @click="applyDirectMxPreset">Direct MX 预设</v-btn>
            </template>
            <v-btn v-if="canManage && activeSection !== 'check' && activeSection !== 'dkim' && activeSection !== 'records'" color="primary" prepend-icon="mdi-content-save-outline" :disabled="!canSave" :loading="saving" @click="save">保存并生成记录</v-btn>
          </div>
        </div>

        <div v-if="activeSection === 'check'" class="section-body">
          <v-alert v-if="dnsCheck?.lastError" type="error" variant="tonal" density="comfortable" class="mb-4">{{ dnsCheck.lastError }}</v-alert>
          <div class="check-summary">
            <div v-for="item in checkItems" :key="`${item.kind}-${item.name}`">
              <v-icon :color="item.status.toLowerCase() === 'match' ? 'success' : 'error'" size="20">{{ item.status.toLowerCase() === 'match' ? 'mdi-check-circle-outline' : 'mdi-alert-circle-outline' }}</v-icon>
              <span><b>{{ item.kind }}</b><small>{{ item.name }}</small></span>
              <span class="check-value">{{ item.actual.join(' · ') || '未发布' }}</span>
            </div>
            <div v-for="warning in checkWarnings" :key="warning" class="is-warning">
              <v-icon color="warning" size="20">mdi-alert-outline</v-icon>
              <span><b>交付提示</b><small>不阻止外发</small></span>
              <span class="check-value">{{ warning }}</span>
            </div>
            <div v-if="!dnsCheck?.validation" class="check-empty">
              <span>尚无 DNS 检查结果，点击右上角「重新检查」运行 Direct MX 检查。</span>
            </div>
          </div>
          <div v-if="dnsCheck?.validation" class="check-table-wrap">
            <v-table density="comfortable">
              <thead><tr><th>检查项</th><th>记录名称</th><th>期望值</th><th>当前发布值</th><th>结果</th></tr></thead>
              <tbody>
                <tr v-for="item in checkItems" :key="`row-${item.kind}-${item.name}`">
                  <td><b>{{ item.kind }}</b></td>
                  <td><code>{{ item.name }}</code></td>
                  <td class="check-value">{{ item.expected }}</td>
                  <td class="check-value">{{ item.actual.join(' · ') || '未发布' }}</td>
                  <td><StatusPill :label="item.status.toLowerCase() === 'match' ? '匹配' : '需处理'" :tone="item.status.toLowerCase() === 'match' ? 'success' : 'danger'" /></td>
                </tr>
              </tbody>
            </v-table>
            <div v-if="checkWarnings.length" class="dns-warnings">
              <h3>交付提示</h3>
              <p v-for="warning in checkWarnings" :key="`w-${warning}`"><v-icon size="18" color="warning">mdi-alert-outline</v-icon>{{ warning }}</p>
            </div>
          </div>
        </div>

        <div v-else-if="activeSection === 'spf'" class="section-body">
          <div class="field-grid">
            <v-switch v-model="settings.spfIncludeMx" color="primary" label="包含 MX 主机" hide-details :disabled="!canManage" />
            <v-switch v-model="settings.spfIncludeA" color="primary" label="包含 A/AAAA 记录" hide-details :disabled="!canManage" />
          </div>
          <div class="field-grid">
            <v-textarea v-model="settings.spfIpv4" label="IPv4 地址" placeholder="203.0.113.25" rows="2" spellcheck="false" hide-details="auto" hint="逗号或换行分隔" persistent-hint :disabled="!canManage" />
            <v-textarea v-model="settings.spfIpv6" label="IPv6 地址" placeholder="2001:db8::25" rows="2" spellcheck="false" hide-details="auto" hint="逗号或换行分隔" persistent-hint :disabled="!canManage" />
          </div>
          <div class="field-grid">
            <v-textarea v-model="settings.spfIncludes" label="include 域名" placeholder="spf.protection.outlook.com" rows="2" spellcheck="false" hide-details="auto" :disabled="!canManage" />
            <v-textarea v-model="settings.spfCustomMechanisms" label="自定义机制" placeholder="exists:%{i}._spf.example.com" rows="2" spellcheck="false" hide-details="auto" :disabled="!canManage" />
          </div>
          <v-select v-model="settings.spfAllPolicy" :items="spfAllPolicyOptions" label="all 策略" hide-details :disabled="!canManage" />
          <div v-if="spfPreview" class="record-preview"><span>生成的 SPF 记录</span><code>{{ spfPreview }}</code></div>
        </div>

        <div v-else-if="activeSection === 'dmarc'" class="section-body">
          <div class="field-grid field-grid--three">
            <v-select v-model="settings.dmarcPolicy" :items="dmarcPolicyOptions" label="主域策略 p" hide-details :disabled="!canManage" />
            <v-select v-model="settings.dmarcSubdomainPolicy" :items="dmarcSubdomainPolicyOptions" label="子域策略 sp" hide-details :disabled="!canManage" />
            <v-text-field v-model.number="settings.dmarcPercentage" type="number" min="0" max="100" label="应用百分比 pct" hide-details :disabled="!canManage" />
          </div>
          <div class="field-grid">
            <v-textarea v-model="settings.dmarcAggregateReportUris" label="聚合报告 rua" placeholder="mailto:dmarc@example.com" rows="2" spellcheck="false" hide-details="auto" :disabled="!canManage" />
            <v-textarea v-model="settings.dmarcForensicReportUris" label="取证报告 ruf" placeholder="mailto:forensic@example.com" rows="2" spellcheck="false" hide-details="auto" :disabled="!canManage" />
          </div>
          <div class="field-grid field-grid--three">
            <v-select v-model="settings.dmarcDkimAlignment" :items="alignmentOptions" label="DKIM 对齐 adkim" hide-details :disabled="!canManage" />
            <v-select v-model="settings.dmarcSpfAlignment" :items="alignmentOptions" label="SPF 对齐 aspf" hide-details :disabled="!canManage" />
            <v-text-field v-model="settings.dmarcFailureOptions" label="失败报告选项 fo" placeholder="0" hide-details :disabled="!canManage" />
          </div>
          <div v-if="dmarcPreview" class="record-preview"><span>生成的 DMARC 记录</span><code>{{ dmarcPreview }}</code></div>
        </div>

        <div v-else-if="activeSection === 'mta'" class="section-body">
          <div class="field-grid field-grid--three">
            <v-select v-model="settings.mtaStsMode" :items="mtaStsModeOptions" label="MTA-STS 模式" hide-details :disabled="!canManage" />
            <v-text-field v-model="settings.mtaStsPolicyId" label="策略 ID" hide-details :disabled="!canManage" />
            <v-text-field v-model.number="settings.mtaStsMaxAgeSeconds" type="number" min="0" max="31557600" label="max_age（秒）" hide-details :disabled="!canManage" />
          </div>
          <v-textarea v-model="settings.mtaStsMxPatterns" label="MX 模式" placeholder="mail.example.com" rows="2" spellcheck="false" hide-details="auto" hint="启用 MTA-STS 后至少一条" persistent-hint :disabled="!canManage" />
          <v-textarea v-model="settings.tlsReportUris" label="TLS-RPT 接收地址" placeholder="mailto:tls-rpt@example.com" rows="2" spellcheck="false" hide-details="auto" :disabled="!canManage" />
          <div v-if="mtaStsPreview" class="record-preview"><span>MTA-STS 记录</span><code>{{ mtaStsPreview }}</code></div>
          <div v-if="tlsRptPreview" class="record-preview"><span>TLS-RPT 记录</span><code>{{ tlsRptPreview }}</code></div>
        </div>

        <div v-else-if="activeSection === 'dane'" class="section-body">
          <v-switch v-model="settings.daneEnabled" color="primary" label="发布 DANE TLSA 记录" hide-details :disabled="!canManage" />
          <v-textarea v-model="settings.daneTlsaRecords" label="TLSA 记录" placeholder="3 1 1 SHA256摘要" rows="3" spellcheck="false" hide-details="auto" hint="每行一条，格式：usage selector matching-type 证书摘要" persistent-hint :disabled="!canManage" />
          <v-alert v-if="settings.daneEnabled" type="warning" variant="tonal" density="compact" class="mt-3">启用前请确认域已部署 DNSSEC，否则 TLSA 记录不会被验证。</v-alert>
        </div>

        <div v-else-if="activeSection === 'dkim'" class="section-body">
          <div class="dkim-body">
            <div class="dkim-controls">
              <v-text-field v-model="dkimSelectorInput" label="DKIM selector" placeholder="s202609" spellcheck="false" hide-details="auto" hint="字母、数字和连字符，最多 63 位" persistent-hint :disabled="!canManage" />
              <v-btn color="primary" prepend-icon="mdi-key-plus" :disabled="!canManage || generatingDkim" :loading="generatingDkim" @click="generateDkim">生成 RSA 2048 DKIM</v-btn>
            </div>
            <v-alert type="warning" variant="tonal" density="compact">重新生成密钥会替换服务器上的私钥，旧 selector 的签名将立即失效。</v-alert>
            <div v-if="dkimRecord" class="record-preview"><span>待发布的 DKIM 记录</span><code>{{ dkimRecord }}</code></div>
            <p v-else class="dkim-empty">尚未生成 DKIM 密钥。</p>
          </div>
        </div>

        <div v-else class="section-body">
          <div class="record-toolbar">
            <span>保存策略后这里会汇总所有需要发布的记录。</span>
            <v-btn v-if="records.length" size="small" variant="tonal" prepend-icon="mdi-content-copy" @click="copyAll">{{ copiedName === '__all__' ? '已复制全部' : '复制全部' }}</v-btn>
          </div>
          <div class="record-list">
            <div v-for="record in records" :key="`${record.name}-${record.type}-${record.value}`" class="record-row">
              <span class="record-kind">{{ record.type }}</span>
              <span class="record-main"><b>{{ record.name }}</b><code>{{ record.value }}</code></span>
              <v-btn size="small" variant="text" :prepend-icon="copiedName === record.name ? 'mdi-check' : 'mdi-content-copy'" @click="copyRecord(record)">{{ copiedName === record.name ? '已复制' : '复制' }}</v-btn>
            </div>
            <div v-if="!records.length" class="record-empty">保存策略后这里会显示需要发布的记录。</div>
          </div>
        </div>
      </v-card>
    </div>
  </section>
</template>
<style scoped>
.dns-workspace { display: grid; grid-template-columns: 260px minmax(0, 1fr); align-items: start; gap: 16px; }
.dns-nav { position: sticky; top: 76px; overflow: hidden; border: 1px solid var(--border); border-radius: 10px; background: #fff; }
.dns-nav button { display: grid; grid-template-columns: 25px minmax(0, 1fr) 18px; align-items: center; gap: 10px; width: 100%; padding: 14px 15px; border: 0; border-bottom: 1px solid var(--border); color: var(--text-secondary); background: transparent; text-align: left; cursor: pointer; }
.dns-nav button:hover { background: #f8fafc; }
.dns-nav button.active { color: #175cb8; background: #f2f7ff; box-shadow: inset 3px 0 #2c72d6; }
.dns-nav button span { display: flex; flex-direction: column; gap: 2px; }
.dns-nav button b { font-size: 13px; }
.dns-nav button small { color: var(--text-muted); font-size: 11px; }
.status-dot { width: 8px; height: 8px; border-radius: 50%; }
.status-dot.is-dirty { background: #e9a23b; box-shadow: 0 0 0 4px #fff4de; }
.nav-note { display: flex; gap: 8px; padding: 12px 14px; color: var(--text-muted); background: #fafbfc; font-size: 11px; line-height: 1.6; }
.nav-note :deep(.v-icon) { flex: none; margin-top: 2px; }
.dns-panel { min-width: 0; overflow: hidden; }
.last-check { color: var(--text-muted); font-size: 11px; }
.check-summary { display: flex; flex-direction: column; border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }
.check-summary > div { display: grid; grid-template-columns: auto minmax(0, 1fr) minmax(0, 1.2fr); align-items: center; gap: 12px; padding: 11px 16px; border-top: 1px solid var(--border); }
.check-summary > div:first-child { border-top: 0; }
.check-summary > div.is-warning { background: #fffbf2; }
.check-summary span { display: flex; flex-direction: column; gap: 2px; }
.check-summary b { color: var(--text); font-size: 12px; }
.check-summary small { color: var(--text-muted); font-size: 10px; }
.check-summary .check-value { overflow-wrap: anywhere; color: var(--text-secondary); font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 10px; }
.check-empty { grid-template-columns: 1fr !important; color: var(--text-muted); text-align: center; }
.check-table-wrap { margin-top: 16px; overflow: hidden; border: 1px solid var(--border); border-radius: 8px; }
.check-value { max-width: 290px; overflow-wrap: anywhere; color: var(--text-secondary) !important; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 10px !important; }
.dns-warnings { padding: 16px 20px; background: #fffbf2; border-top: 1px solid #eadcb9; }
.dns-warnings h3 { margin: 0 0 8px; color: #765317; font-size: 13px; }
.dns-warnings p { display: flex; align-items: center; gap: 8px; margin: 5px 0; color: #8a6116; font-size: 11px; }
.section-header { display: flex; align-items: center; justify-content: space-between; gap: 14px; flex-wrap: wrap; padding: 18px 22px; border-bottom: 1px solid var(--border); }
.section-header span { color: var(--text-muted); font-size: 11px; letter-spacing: .08em; text-transform: uppercase; }
.section-header h2 { margin: 3px 0 0; color: var(--text); font-size: 17px; }
.section-header p { margin: 4px 0 0; color: var(--text-muted); font-size: 12px; }
.section-body { padding: 20px 22px; }
.head-actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.field-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; margin-bottom: 14px; }
.field-grid--three { grid-template-columns: repeat(3, minmax(0, 1fr)); }
.record-preview { display: flex; flex-direction: column; gap: 5px; margin-top: 14px; padding: 12px 14px; background: #f5f7fa; border: 1px solid var(--border); border-radius: 8px; }
.record-preview span { color: var(--text-muted); font-size: 10px; }
.record-preview code { overflow-wrap: anywhere; color: var(--text-secondary); font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 11px; }
.dkim-body { display: grid; gap: 14px; }
.dkim-controls { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 14px; align-items: start; }
.dkim-empty { margin: 0; color: var(--text-muted); font-size: 12px; }
.record-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; margin-bottom: 14px; }
.record-toolbar span { color: var(--text-muted); font-size: 12px; }
.record-list { overflow: hidden; border: 1px solid var(--border); border-radius: 8px; }
.record-row { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; gap: 12px; align-items: center; padding: 12px 20px; border-top: 1px solid var(--border); }
.record-row:first-child { border-top: 0; }
.record-kind { padding: 3px 7px; color: #436385; background: #edf3f9; border-radius: 5px; font-size: 10px; font-weight: 700; }
.record-main { display: flex; min-width: 0; flex-direction: column; gap: 4px; }
.record-main b { color: var(--text); font-size: 12px; }
.record-main code { overflow-wrap: anywhere; color: var(--text-secondary); font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 10px; }
.record-empty { padding: 34px 20px; color: var(--text-muted); text-align: center; }
@media (max-width: 1100px) {
  .dns-workspace { grid-template-columns: 1fr; }
  .dns-nav { position: static; display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .dns-nav button { border-right: 1px solid var(--border); }
  .nav-note { grid-column: 1 / -1; }
  .field-grid--three { grid-template-columns: 1fr 1fr; }
}
@media (max-width: 700px) {
  .dns-nav { grid-template-columns: 1fr; }
  .field-grid, .field-grid--three, .dkim-controls { grid-template-columns: 1fr; }
}
</style>