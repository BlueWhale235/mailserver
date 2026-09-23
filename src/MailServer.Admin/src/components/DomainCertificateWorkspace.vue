<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import StatusPill from './StatusPill.vue'
import {
  bindCertificatePath,
  certificateUploadBody,
  deleteCertificate,
  inspectCertificatePath,
  inspectCertificateUpload,
  isCertificateUsable,
  maximumCertificateBytes,
  maximumPrivateKeyBytes,
  pemTextFile,
  uploadCertificate,
  type DomainCertificateInspection,
  type DomainCertificateModel,
} from '../lib/domainCertificate'

interface OutboundAddress {
  address: string
  interfaceName: string
  addressFamily: string
}

const props = defineProps<{
  domainId: string
  domainName: string
  mailHostName: string
  certificate: DomainCertificateModel | null
  outboundAddresses: OutboundAddress[]
  canManage: boolean
}>()

const emit = defineEmits<{
  changed: [certificate: DomainCertificateModel | null]
  dirtyChanged: [dirty: boolean]
  savingChanged: [saving: boolean]
}>()

type SourceMode = 'upload' | 'paste' | 'path'
type SectionKey = 'current' | 'configure' | 'outbound'

const sections: Array<{ key: SectionKey; title: string; description: string; icon: string }> = [
  { key: 'current', title: '当前证书', description: '查看已生效证书与到期时间', icon: 'mdi-certificate-outline' },
  { key: 'configure', title: '配置或替换', description: '上传、粘贴或绑定服务器路径', icon: 'mdi-file-upload-outline' },
  { key: 'outbound', title: '出站地址', description: 'Direct MX 可绑定网络接口', icon: 'mdi-network-outline' },
]

const route = useRoute()
const router = useRouter()

const sectionKeys: SectionKey[] = ['current', 'configure', 'outbound']
function normalizeSection(value: unknown): SectionKey {
  return sectionKeys.includes(value as SectionKey) ? (value as SectionKey) : 'current'
}

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
  if (route.params.tab === 'cert' && String(route.params.section ?? '') === value && clean) return
  void router.replace({ name: 'domain-detail', params: { id: props.domainId, tab: 'cert', section: value }, query })
}, { immediate: true })

const currentSection = computed(() => sections.find(section => section.key === activeSection.value)!)

const mode = ref<SourceMode>('upload')
const busy = ref(false)
const error = ref('')
const notice = ref('')
const inspection = ref<DomainCertificateInspection | null>(null)
const inspectedSignature = ref('')

const certificateFile = ref<File | null>(null)
const privateKeyFile = ref<File | null>(null)
const privateKeyPassword = ref('')
const pastedCertificate = ref('')
const pastedPrivateKey = ref('')
const pastedPrivateKeyPassword = ref('')
const certificatePath = ref('')
const privateKeyPath = ref('')
const deleteOpen = ref(false)
const deleteConfirmation = ref('')

const sourceItems: Array<{ value: SourceMode; label: string; icon: string }> = [
  { value: 'upload', label: '上传文件', icon: 'mdi-file-upload-outline' },
  { value: 'paste', label: '粘贴 PEM', icon: 'mdi-content-paste' },
  { value: 'path', label: '服务器路径', icon: 'mdi-folder-outline' },
]

const term = computed(() => props.mailHostName || props.domainName)
const absolutePathPattern = /^(?:[a-zA-Z]:[\\/]|\\\\|\/)/
const inspectSignature = computed(() => {
  if (mode.value === 'upload') return `upload:${certificateFile.value?.name ?? ''}:${certificateFile.value?.size ?? 0}:${privateKeyFile.value?.name ?? ''}:${privateKeyFile.value?.size ?? 0}:${hash(privateKeyPassword.value)}`
  if (mode.value === 'paste') return `paste:${hash(pastedCertificate.value)}:${hash(pastedPrivateKey.value)}:${hash(pastedPrivateKeyPassword.value)}`
  return `path:${certificatePath.value.trim()}:${privateKeyPath.value.trim()}`
})
const inspectionIsCurrent = computed(() => Boolean(inspection.value) && inspectedSignature.value === inspectSignature.value)
const usableInspection = computed(() => inspectionIsCurrent.value && isCertificateUsable(inspection.value))
const canInspect = computed(() => props.canManage && !busy.value && hasRequiredInput())
const canApply = computed(() => props.canManage && !busy.value && usableInspection.value)
const applyLabel = computed(() => mode.value === 'path' ? '绑定并启用' : '上传并启用')
const deleteConfirmed = computed(() => deleteConfirmation.value.trim() === term.value)
const dirty = computed(() => {
  if (mode.value === 'upload') {
    return Boolean(certificateFile.value || privateKeyFile.value || privateKeyPassword.value)
  }
  if (mode.value === 'paste') {
    return Boolean(pastedCertificate.value.trim() || pastedPrivateKey.value.trim() || pastedPrivateKeyPassword.value)
  }
  const currentCertificatePath = props.certificate?.source === 'ExternalPath' ? props.certificate.certificatePath ?? '' : ''
  const currentPrivateKeyPath = props.certificate?.source === 'ExternalPath' ? props.certificate.privateKeyPath ?? '' : ''
  return certificatePath.value.trim() !== currentCertificatePath || privateKeyPath.value.trim() !== currentPrivateKeyPath
})

watch(dirty, value => emit('dirtyChanged', value), { immediate: true })
watch(busy, value => emit('savingChanged', value), { immediate: true })

watch([mode, certificateFile, privateKeyFile, privateKeyPassword, pastedCertificate, pastedPrivateKey, pastedPrivateKeyPassword, certificatePath, privateKeyPath], () => {
  if (!inspectionIsCurrent.value) inspection.value = null
})

watch([mode, () => props.certificate], () => {
  error.value = ''
  notice.value = ''
}, { deep: false })

watch(() => props.certificate, certificate => {
  if (certificate?.source === 'ExternalPath') {
    certificatePath.value = certificate.certificatePath ?? ''
    privateKeyPath.value = certificate.privateKeyPath ?? ''
  } else if (!certificate) {
    certificatePath.value = ''
    privateKeyPath.value = ''
  }
}, { immediate: true })

function hasRequiredInput(): boolean {
  if (mode.value === 'upload') return Boolean(certificateFile.value && privateKeyFile.value)
  if (mode.value === 'paste') return pastedCertificate.value.trim().length > 0 && pastedPrivateKey.value.trim().length > 0
  return certificatePath.value.trim().length > 0 && privateKeyPath.value.trim().length > 0
}

function hash(value: string): number {
  let result = 0
  for (let index = 0; index < value.length; index += 1) result = (result * 31 + value.charCodeAt(index)) | 0
  return result
}

function formatBytes(value: number): string {
  if (value < 1024) return `${value} B`
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KiB`
  return `${(value / 1024 / 1024).toFixed(2)} MiB`
}

function sourceLabel(source?: string): string {
  if (source === 'ManagedUpload') return '上传到服务器托管目录'
  if (source === 'ExternalPath') return '服务器文件路径'
  return source || '—'
}

function date(value?: string): string {
  return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
}

function sanNames(value: string): string {
  return value.split(/\r?\n/).filter(Boolean).join(' · ') || '—'
}

function validityTone(value?: string): 'success' | 'warning' | 'danger' | 'neutral' {
  if (!value) return 'neutral'
  const remaining = new Date(value).getTime() - Date.now()
  if (remaining <= 0) return 'danger'
  if (remaining <= 30 * 24 * 60 * 60 * 1000) return 'warning'
  return 'success'
}

function validityLabel(value?: string): string {
  const tone = validityTone(value)
  if (tone === 'danger') return '已过期'
  if (tone === 'warning') return '即将到期'
  if (tone === 'neutral') return '状态未知'
  return '有效'
}

function buildForm(): FormData | null {
  if (mode.value === 'upload') {
    if (!certificateFile.value || !privateKeyFile.value) return null
    return certificateUploadBody(certificateFile.value, privateKeyFile.value, privateKeyPassword.value)
  }
  if (mode.value !== 'paste') return null
  return certificateUploadBody(
    pemTextFile(pastedCertificate.value, 'pasted-chain.pem'),
    pemTextFile(pastedPrivateKey.value, 'pasted-key.pem'),
    pastedPrivateKeyPassword.value,
  )
}

function validateBeforeRequest(): string {
  if (mode.value === 'upload') {
    if (!certificateFile.value || !privateKeyFile.value) return '请先选择证书链和私钥文件。'
    if (certificateFile.value.size === 0) return '证书链文件为空。'
    if (certificateFile.value.size > maximumCertificateBytes) return `证书链不能超过 ${formatBytes(maximumCertificateBytes)}。`
    if (privateKeyFile.value.size === 0) return '私钥文件为空。'
    if (privateKeyFile.value.size > maximumPrivateKeyBytes) return `私钥不能超过 ${formatBytes(maximumPrivateKeyBytes)}。`
    return ''
  }
  if (mode.value === 'paste') {
    if (!pastedCertificate.value.includes('BEGIN CERTIFICATE')) return '粘贴的证书链必须包含 BEGIN CERTIFICATE 标记。'
    if (!/BEGIN [A-Z ]*PRIVATE KEY/.test(pastedPrivateKey.value)) return '粘贴的私钥必须包含 PEM 私钥标记。'
    return ''
  }
  if (!absolutePathPattern.test(certificatePath.value.trim())) return '证书链必须是绝对路径，例如 D:\\certs\\fullchain.pem。'
  if (!absolutePathPattern.test(privateKeyPath.value.trim())) return '私钥必须是绝对路径，例如 D:\\certs\\private.key。'
  return ''
}

async function runInspection() {
  if (!canInspect.value) return
  const validationError = validateBeforeRequest()
  if (validationError) { error.value = validationError; return }
  busy.value = true
  error.value = ''
  notice.value = ''
  const signature = inspectSignature.value
  try {
    inspection.value = mode.value === 'path'
      ? await inspectCertificatePath(props.domainId, certificatePath.value.trim(), privateKeyPath.value.trim())
      : await inspectCertificateUpload(props.domainId, buildForm()!)
    inspectedSignature.value = signature
    notice.value = isCertificateUsable(inspection.value) ? '证书解析完成，已通过启用前检查。' : '证书解析完成，但未通过启用前检查。'
  } catch (cause) {
    inspection.value = null
    inspectedSignature.value = ''
    error.value = cause instanceof Error ? cause.message : '证书解析失败'
  } finally {
    busy.value = false
  }
}

async function applyCertificate() {
  if (!canApply.value) return
  busy.value = true
  error.value = ''
  notice.value = ''
  try {
    const saved = mode.value === 'path'
      ? await bindCertificatePath(props.domainId, certificatePath.value.trim(), privateKeyPath.value.trim())
      : await uploadCertificate(props.domainId, buildForm()!)
    inspection.value = null
    inspectedSignature.value = ''
    notice.value = mode.value === 'path' ? '已绑定服务器路径证书并立即生效。' : '证书已上传并立即生效。'
    // 启用成功后回到「当前证书」，让管理员立即核对生效结果，而不是停留在空的表单上。
    activeSection.value = 'current'
    resetSecrets()
    pastedCertificate.value = ''
    emit('changed', saved)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '证书保存失败'
  } finally {
    busy.value = false
  }
}

async function removeCertificate() {
  if (!props.canManage || !deleteConfirmed.value || busy.value) return
  busy.value = true
  error.value = ''
  notice.value = ''
  try {
    await deleteCertificate(props.domainId)
    deleteOpen.value = false
    deleteConfirmation.value = ''
    notice.value = '证书已删除。请尽快配置新证书，否则 SMTP/IMAP 的 TLS 握手将失败。'
    emit('changed', null)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '证书删除失败'
  } finally {
    busy.value = false
  }
}

function resetSecrets() {
  privateKeyPassword.value = ''
  pastedPrivateKey.value = ''
  pastedPrivateKeyPassword.value = ''
  certificateFile.value = null
  privateKeyFile.value = null
}

function clearInputs() {
  resetSecrets()
  pastedCertificate.value = ''
  inspection.value = null
  inspectedSignature.value = ''
  error.value = ''
  notice.value = ''
}

defineExpose({ clearInputs })
</script>

<template>
  <div class="certificate-workspace">
    <nav class="certificate-nav" aria-label="TLS 证书分区">
      <button v-for="section in sections" :key="section.key" :class="{ active: activeSection === section.key }" @click="activeSection = section.key">
        <v-icon size="20">{{ section.icon }}</v-icon>
        <span><b>{{ section.title }}</b><small>{{ section.description }}</small></span>
        <v-icon v-if="section.key === 'current' && certificate" size="16" color="success">mdi-check-circle-outline</v-icon>
        <i v-else-if="section.key === 'current'" class="status-dot is-warning" title="尚未配置证书" />
      </button>
      <div class="nav-note"><v-icon size="17">mdi-shield-lock-outline</v-icon><span>证书内容只提交到当前服务器，不会写入浏览器存储。</span></div>
    </nav>

    <v-card flat class="panel certificate-panel">
      <div class="section-header">
        <div>
          <span>TLS 证书</span>
          <h2>{{ currentSection.title }}</h2>
          <p>{{ currentSection.description }}</p>
        </div>
        <template v-if="activeSection === 'current'">
          <StatusPill v-if="certificate" :label="validityLabel(certificate.notAfter)" :tone="validityTone(certificate.notAfter)" />
          <StatusPill :label="certificate ? '已配置' : '未配置'" :tone="certificate ? 'success' : 'warning'" />
        </template>
        <StatusPill v-else-if="activeSection === 'configure'" :label="usableInspection ? '可启用' : '待校验'" :tone="usableInspection ? 'success' : 'neutral'" />
        <StatusPill v-else label="只读" tone="neutral" />
      </div>

      <v-alert v-if="error" type="error" variant="tonal" class="ma-5" closable @click:close="error = ''">{{ error }}</v-alert>
      <v-alert v-if="notice" type="success" variant="tonal" class="ma-5" closable @click:close="notice = ''">{{ notice }}</v-alert>
      <v-progress-linear v-if="busy" indeterminate color="primary" />

      <div v-if="activeSection === 'current'" class="section-body">
        <v-alert v-if="!certificate" type="warning" variant="tonal">
          该域尚未绑定 TLS 证书。请切换到「配置或替换」上传文件、粘贴 PEM 或绑定服务器路径。
        </v-alert>
        <div v-else>
          <dl class="certificate-grid">
            <div><dt>来源</dt><dd>{{ sourceLabel(certificate.source) }}</dd></div>
            <div><dt>主题</dt><dd>{{ certificate.subject }}</dd></div>
            <div><dt>签发机构</dt><dd>{{ certificate.issuer }}</dd></div>
            <div><dt>备用名称</dt><dd>{{ sanNames(certificate.subjectAlternativeNames) }}</dd></div>
            <div><dt>生效时间</dt><dd>{{ date(certificate.notBefore) }}</dd></div>
            <div><dt>到期时间</dt><dd>{{ date(certificate.notAfter) }}</dd></div>
            <div><dt>指纹</dt><dd><code>{{ certificate.thumbprint }}</code></dd></div>
            <div><dt>更新时间</dt><dd>{{ date(certificate.updatedAt) }}</dd></div>
          </dl>
          <div v-if="canManage" class="panel-action">
            <span>替换证书会立即让新证书生效；删除后相关 TLS 服务将无法完成握手。</span>
            <v-btn color="error" variant="tonal" size="small" prepend-icon="mdi-delete-outline" :disabled="busy" @click="deleteOpen = true">删除证书</v-btn>
          </div>
        </div>
      </div>

      <div v-else-if="activeSection === 'configure'" class="section-body">
        <v-alert v-if="!canManage" type="info" variant="tonal">当前账号仅可查看证书信息，缺少 domain.manage 权限。</v-alert>
        <div v-else>
          <div class="source-switch">
            <v-btn-toggle v-model="mode" mandatory color="primary" variant="outlined" divided>
              <v-btn v-for="item in sourceItems" :key="item.value" :value="item.value" :prepend-icon="item.icon">{{ item.label }}</v-btn>
            </v-btn-toggle>
          </div>

          <div v-if="mode === 'upload'" class="source-form">
            <v-file-input v-model="certificateFile" label="证书链（PEM/CRT/CER）" accept=".pem,.crt,.cer" prepend-icon="mdi-certificate-outline" density="comfortable" hide-details="auto" :hint="`包含完整签发链，最大 ${formatBytes(maximumCertificateBytes)}`" persistent-hint />
            <v-file-input v-model="privateKeyFile" label="私钥（PEM/KEY）" accept=".pem,.key" prepend-icon="mdi-key-outline" density="comfortable" hide-details="auto" :hint="`私钥只保存在服务器托管目录，最大 ${formatBytes(maximumPrivateKeyBytes)}`" persistent-hint />
            <v-text-field v-model="privateKeyPassword" label="私钥密码（如私钥已加密）" type="password" autocomplete="new-password" density="comfortable" hide-details="auto" hint="留空表示私钥未加密" persistent-hint />
          </div>

          <div v-else-if="mode === 'paste'" class="source-form">
            <v-textarea v-model="pastedCertificate" label="证书链 PEM" rows="6" auto-grow spellcheck="false" placeholder="-----BEGIN CERTIFICATE-----" hide-details="auto" hint="按证书服务商给出的完整链依次粘贴，含中间证书" persistent-hint />
            <v-textarea v-model="pastedPrivateKey" label="私钥 PEM" rows="5" auto-grow spellcheck="false" placeholder="-----BEGIN PRIVATE KEY-----" hide-details="auto" hint="内容只随本次请求提交，不会保存到浏览器" persistent-hint />
            <v-text-field v-model="pastedPrivateKeyPassword" label="私钥密码（如有）" type="password" autocomplete="new-password" hide-details="auto" />
          </div>

          <div v-else class="source-form">
            <v-text-field v-model="certificatePath" label="证书链绝对路径" placeholder="D:\\certs\\fullchain.pem" spellcheck="false" hide-details="auto" hint="服务器本地路径，需运行账号可读" persistent-hint />
            <v-text-field v-model="privateKeyPath" label="私钥绝对路径" placeholder="D:\\certs\\private.key" spellcheck="false" hide-details="auto" hint="服务器本地路径，不会复制文件内容" persistent-hint />
            <v-alert type="info" variant="tonal" density="compact">绑定路径后由外部工具负责轮换文件，更新内容无需再次操作后台。</v-alert>
          </div>

          <div class="source-actions">
            <v-btn variant="outlined" color="primary" prepend-icon="mdi-magnify-scan" :disabled="!canInspect" :loading="busy" @click="runInspection">解析预览</v-btn>
            <v-btn color="primary" prepend-icon="mdi-shield-check-outline" :disabled="!canApply" :loading="busy" @click="applyCertificate">{{ applyLabel }}</v-btn>
            <span v-if="!inspectionIsCurrent" class="source-actions__hint">需先解析预览并通过全部检查后才能启用</span>
          </div>

          <div v-if="inspection" class="inspection" :class="{ 'is-usable': usableInspection }">
            <div class="inspection__head">
              <div><h3>解析结果</h3><p>{{ inspection.subject }}</p></div>
              <StatusPill :label="usableInspection ? '可以启用' : '不可启用'" :tone="usableInspection ? 'success' : 'danger'" />
            </div>
            <dl class="inspection__grid">
              <div><dt>签发机构</dt><dd>{{ inspection.issuer }}</dd></div>
              <div><dt>备用名称</dt><dd>{{ inspection.subjectAlternativeNames.join(' · ') || '—' }}</dd></div>
              <div><dt>生效时间</dt><dd>{{ date(inspection.notBefore) }}</dd></div>
              <div><dt>到期时间</dt><dd>{{ date(inspection.notAfter) }}</dd></div>
              <div><dt>指纹</dt><dd><code>{{ inspection.thumbprint }}</code></dd></div>
            </dl>
            <ul class="inspection__checks">
              <li><v-icon :color="inspection.hasMatchingPrivateKey ? 'success' : 'error'" size="18">{{ inspection.hasMatchingPrivateKey ? 'mdi-check-circle-outline' : 'mdi-close-circle-outline' }}</v-icon>私钥与证书匹配</li>
              <li><v-icon :color="inspection.supportsServerAuthentication ? 'success' : 'error'" size="18">{{ inspection.supportsServerAuthentication ? 'mdi-check-circle-outline' : 'mdi-close-circle-outline' }}</v-icon>支持 TLS 服务器身份验证</li>
              <li><v-icon :color="inspection.coversMailHostName ? 'success' : 'error'" size="18">{{ inspection.coversMailHostName ? 'mdi-check-circle-outline' : 'mdi-close-circle-outline' }}</v-icon>SAN 覆盖 {{ term }}</li>
            </ul>
          </div>
        </div>
      </div>

      <div v-else class="section-body">
        <div class="outbound-list">
          <div v-for="address in outboundAddresses" :key="address.address">
            <v-icon>mdi-network-outline</v-icon>
            <span><b>{{ address.address }}</b><small>{{ address.interfaceName }}</small></span>
            <StatusPill :label="address.addressFamily" tone="neutral" />
          </div>
          <div v-if="!outboundAddresses.length" class="outbound-empty">未检测到可绑定的出站地址</div>
        </div>
      </div>
    </v-card>

    <v-dialog v-model="deleteOpen" max-width="560" :persistent="busy">
      <v-card title="删除 TLS 证书" prepend-icon="mdi-alert-outline">
        <v-card-text>
          <v-alert type="error" variant="tonal" density="compact" class="mb-4">删除后 SMTP 与 IMAP 将无法完成 TLS 握手，直到重新配置证书。</v-alert>
          <p class="dialog-hint">请输入 <b>{{ term }}</b> 以确认删除。</p>
          <v-text-field v-model="deleteConfirmation" :label="`输入 ${term} 确认`" autocomplete="off" autofocus class="mt-3" @keyup.enter="removeCertificate" />
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="busy" @click="deleteOpen = false; deleteConfirmation = ''">取消</v-btn>
          <v-btn color="error" :disabled="!deleteConfirmed" :loading="busy" @click="removeCertificate">确认删除</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </div>
</template>
<style scoped>
.certificate-workspace { display: grid; grid-template-columns: 260px minmax(0, 1fr); align-items: start; gap: 16px; }
.certificate-nav { position: sticky; top: 76px; overflow: hidden; border: 1px solid var(--border); border-radius: 10px; background: #fff; }
.certificate-nav button { display: grid; grid-template-columns: 25px minmax(0, 1fr) 18px; align-items: center; gap: 10px; width: 100%; padding: 14px 15px; border: 0; border-bottom: 1px solid var(--border); color: var(--text-secondary); background: transparent; text-align: left; cursor: pointer; }
.certificate-nav button:hover { background: #f8fafc; }
.certificate-nav button.active { color: #175cb8; background: #f2f7ff; box-shadow: inset 3px 0 #2c72d6; }
.certificate-nav button span { display: flex; flex-direction: column; gap: 2px; }
.certificate-nav button b { font-size: 13px; }
.certificate-nav button small { color: var(--text-muted); font-size: 11px; }
.nav-note { display: flex; gap: 8px; padding: 12px 14px; color: var(--text-muted); background: #fafbfc; font-size: 11px; line-height: 1.6; }
.nav-note :deep(.v-icon) { flex: none; margin-top: 2px; }
.certificate-panel { min-width: 0; overflow: hidden; }
.section-header { display: flex; align-items: center; justify-content: space-between; gap: 14px; flex-wrap: wrap; padding: 18px 22px; border-bottom: 1px solid var(--border); }
.section-header span { color: var(--text-muted); font-size: 11px; letter-spacing: .08em; text-transform: uppercase; }
.section-header h2 { margin: 3px 0 0; color: var(--text); font-size: 17px; }
.section-header p { margin: 4px 0 0; color: var(--text-muted); font-size: 12px; }
.section-body { padding: 20px 22px; }
.certificate-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); margin: 0; border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }
.certificate-grid div { min-width: 0; padding: 13px 16px; border-right: 1px solid var(--border); border-bottom: 1px solid var(--border); }
.certificate-grid div:nth-child(4n) { border-right: 0; }
.certificate-grid div:nth-last-child(-n + 4) { border-bottom: 0; }
.certificate-grid dt { color: var(--text-muted); font-size: 10px; }
.certificate-grid dd { margin: 5px 0 0; overflow-wrap: anywhere; color: var(--text); font-size: 12px; font-weight: 600; }
.certificate-grid code { color: var(--text-secondary); font-size: 10px; }
.panel-action { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-top: 18px; padding: 13px 16px; background: #fff8f7; border: 1px solid #f2d6d3; border-radius: 8px; }
.panel-action span { color: #8a4b46; font-size: 11px; line-height: 1.6; }
.source-switch { padding: 0 0 16px; }
.source-form { display: grid; gap: 18px; }
.source-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; margin-top: 18px; }
.source-actions__hint { color: var(--text-muted); font-size: 11px; }
.inspection { margin-top: 18px; overflow: hidden; border: 1px solid #f0d9a8; border-radius: 8px; background: #fffdf7; }
.inspection.is-usable { border-color: #bfe0c8; background: #f7fdf9; }
.inspection__head { display: flex; align-items: center; justify-content: space-between; gap: 14px; padding: 14px 18px; }
.inspection__head h3 { margin: 0; color: var(--text); font-size: 14px; }
.inspection__head p { margin: 4px 0 0; overflow-wrap: anywhere; color: var(--text-muted); font-size: 11px; }
.inspection__grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); margin: 0; border-top: 1px solid var(--border); }
.inspection__grid div { min-width: 0; padding: 10px 18px; }
.inspection__grid dt { color: var(--text-muted); font-size: 10px; }
.inspection__grid dd { margin: 4px 0 0; overflow-wrap: anywhere; color: var(--text); font-size: 11px; font-weight: 600; }
.inspection__grid code { font-size: 10px; }
.inspection__checks { display: grid; gap: 8px; margin: 0; padding: 12px 18px 16px; list-style: none; border-top: 1px solid var(--border); }
.inspection__checks li { display: flex; align-items: center; gap: 8px; color: var(--text-secondary); font-size: 12px; }
.outbound-list { overflow: hidden; border: 1px solid var(--border); border-radius: 8px; }
.outbound-list > div { display: grid; grid-template-columns: auto 1fr auto; gap: 12px; align-items: center; min-height: 62px; padding: 10px 18px; border-bottom: 1px solid var(--border); }
.outbound-list > div:last-child { border-bottom: 0; }
.outbound-list span { display: flex; flex-direction: column; }
.outbound-list b { color: var(--text); font-size: 12px; }
.outbound-list small { color: var(--text-muted); font-size: 10px; }
.outbound-list :deep(.v-icon) { color: #56779e; }
.outbound-empty { grid-template-columns: 1fr !important; color: var(--text-muted); font-size: 12px; }
.dialog-hint { margin: 14px 0 0; color: var(--text-muted); font-size: 12px; line-height: 1.6; }
@media (max-width: 1100px) {
  .certificate-workspace { grid-template-columns: 1fr; }
  .certificate-nav { position: static; display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .certificate-nav button { border-right: 1px solid var(--border); }
  .nav-note { grid-column: 1 / -1; }
}
@media (max-width: 900px) {
  .certificate-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .certificate-grid div:nth-child(2n) { border-right: 0; }
}
@media (max-width: 700px) {
  .certificate-nav { grid-template-columns: 1fr; }
  .certificate-grid, .inspection__grid { grid-template-columns: 1fr; }
  .certificate-grid div { border-right: 0; }
  .source-switch :deep(.v-btn-toggle) { flex-direction: column; }
  .panel-action { flex-direction: column; align-items: stretch; }
}
</style>
