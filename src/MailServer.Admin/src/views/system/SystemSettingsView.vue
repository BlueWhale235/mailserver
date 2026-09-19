<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import PageHeader from '../../components/PageHeader.vue'
import StatusPill from '../../components/StatusPill.vue'
import { ApiError, api, jsonBody } from '../../lib/api'
import { usePermission } from '../../composables/usePermission'

type SectionKey = 'delivery' | 'smtp' | 'mailboxes' | 'certificates'

interface DeliverySettings {
  batchSize: number
  leaseSeconds: number
  maximumAttempts: number
  pollIntervalSeconds: number
}

interface SmtpSettings {
  commandTimeoutSeconds: number
}

interface MailboxSettings {
  defaultFolders: string[]
}

interface CertificateSettings {
  defaultMailHostName: string | null
  allowSingleCertificateFallback: boolean
  availableMailHostNames: string[]
  effectiveDefaultMailHostName: string | null
}

const { can } = usePermission()
const sections: Array<{ key: SectionKey; title: string; description: string; icon: string }> = [
  { key: 'delivery', title: '投递调度', description: '批量、租约与重试节奏', icon: 'mdi-send-clock-outline' },
  { key: 'smtp', title: 'SMTP 会话', description: '命令阶段超时控制', icon: 'mdi-email-fast-outline' },
  { key: 'mailboxes', title: '默认文件夹', description: '新邮箱初始化目录', icon: 'mdi-folder-multiple-outline' },
  { key: 'certificates', title: '证书选择', description: '全局证书后备策略', icon: 'mdi-certificate-outline' },
]
const systemFolders = ['INBOX', 'Sent', 'Drafts', 'Trash', 'Junk', 'Archives']

const activeSection = ref<SectionKey>('delivery')
const delivery = ref<DeliverySettings>({ batchSize: 20, leaseSeconds: 120, maximumAttempts: 5, pollIntervalSeconds: 5 })
const smtp = ref<SmtpSettings>({ commandTimeoutSeconds: 300 })
const mailboxes = ref<MailboxSettings>({ defaultFolders: [...systemFolders] })
const certificates = ref<CertificateSettings>({ defaultMailHostName: null, allowSingleCertificateFallback: false, availableMailHostNames: [], effectiveDefaultMailHostName: null })
const baselines = ref<Record<SectionKey, string>>({ delivery: '', smtp: '', mailboxes: '', certificates: '' })
const loaded = ref<Record<SectionKey, boolean>>({ delivery: false, smtp: false, mailboxes: false, certificates: false })
const sectionErrors = ref<Record<SectionKey, string>>({ delivery: '', smtp: '', mailboxes: '', certificates: '' })
const loading = ref(false)
const savingSection = ref<SectionKey | null>(null)
const notice = ref('')
const newFolder = ref('')
const folderError = ref('')
const reloadConfirmOpen = ref(false)

const snapshots = computed<Record<SectionKey, string>>(() => ({
  delivery: JSON.stringify(delivery.value),
  smtp: JSON.stringify(smtp.value),
  mailboxes: JSON.stringify(mailboxes.value),
  certificates: JSON.stringify({
    defaultMailHostName: certificates.value.defaultMailHostName || null,
    allowSingleCertificateFallback: certificates.value.allowSingleCertificateFallback,
    availableMailHostNames: certificates.value.availableMailHostNames,
    effectiveDefaultMailHostName: certificates.value.effectiveDefaultMailHostName,
  }),
}))
const dirtySections = computed(() => sections.filter(section => loaded.value[section.key] && snapshots.value[section.key] !== baselines.value[section.key]).map(section => section.key))
const dirtyCount = computed(() => dirtySections.value.length)
const currentDirty = computed(() => dirtySections.value.includes(activeSection.value))
const currentLoaded = computed(() => loaded.value[activeSection.value])
const currentError = computed(() => sectionErrors.value[activeSection.value])
const currentSection = computed(() => sections.find(section => section.key === activeSection.value)!)

const deliveryErrors = computed(() => ({
  batchSize: integerError(delivery.value.batchSize, 1, 200),
  leaseSeconds: integerError(delivery.value.leaseSeconds, 10, 3600),
  maximumAttempts: integerError(delivery.value.maximumAttempts, 1, 100),
  pollIntervalSeconds: integerError(delivery.value.pollIntervalSeconds, 1, 300),
}))
const smtpError = computed(() => integerError(smtp.value.commandTimeoutSeconds, 5, 3600))
const certificateHostError = computed(() => hostNameError(certificates.value.defaultMailHostName))
const sectionValid = computed<Record<SectionKey, boolean>>(() => ({
  delivery: Object.values(deliveryErrors.value).every(value => !value),
  smtp: !smtpError.value,
  mailboxes: mailboxes.value.defaultFolders.length >= systemFolders.length,
  certificates: !certificateHostError.value,
}))
const currentValid = computed(() => sectionValid.value[activeSection.value])

const customFolders = computed(() => mailboxes.value.defaultFolders.filter(folder => !isSystemFolder(folder)))
const normalizedCertificateHost = computed(() => normalizeHostName(certificates.value.defaultMailHostName))
const draftEffectiveCertificate = computed(() => {
  const configured = normalizedCertificateHost.value
  if (configured && certificates.value.availableMailHostNames.some(host => host.toLowerCase() === configured)) return configured
  if (!configured && certificates.value.allowSingleCertificateFallback && certificates.value.availableMailHostNames.length === 1) return certificates.value.availableMailHostNames[0]!
  return null
})
const configuredCertificateMissing = computed(() => Boolean(normalizedCertificateHost.value) && !draftEffectiveCertificate.value)

function integerError(value: number, minimum: number, maximum: number) {
  const number = Number(value)
  if (String(value ?? '').trim() === '' || !Number.isInteger(number)) return '请输入整数'
  if (number < minimum || number > maximum) return `允许范围 ${minimum} 至 ${maximum}`
  return ''
}

function normalizeHostName(value: string | null) {
  return String(value ?? '').trim().replace(/\.+$/, '').toLowerCase()
}

function hostNameError(value: string | null) {
  const normalized = normalizeHostName(value)
  if (!normalized) return ''
  if (normalized.length > 255 || normalized.includes(' ') || normalized.includes('..') || /[^a-z0-9.-]/.test(normalized)) return '请输入有效的邮件主机名，例如 mail.example.com'
  return ''
}

function isSystemFolder(folder: string) {
  return systemFolders.some(item => item.toLowerCase() === folder.toLowerCase())
}

function acceptDelivery(value: DeliverySettings) {
  delivery.value = globalThis.structuredClone(value)
  baselines.value.delivery = JSON.stringify(delivery.value)
  loaded.value.delivery = true
}

function acceptSmtp(value: SmtpSettings) {
  smtp.value = globalThis.structuredClone(value)
  baselines.value.smtp = JSON.stringify(smtp.value)
  loaded.value.smtp = true
}

function acceptMailboxes(value: MailboxSettings) {
  mailboxes.value = { defaultFolders: [...value.defaultFolders] }
  baselines.value.mailboxes = JSON.stringify(mailboxes.value)
  loaded.value.mailboxes = true
}

function acceptCertificates(value: CertificateSettings) {
  certificates.value = {
    defaultMailHostName: value.defaultMailHostName ?? null,
    allowSingleCertificateFallback: value.allowSingleCertificateFallback,
    availableMailHostNames: [...(value.availableMailHostNames ?? [])],
    effectiveDefaultMailHostName: value.effectiveDefaultMailHostName ?? null,
  }
  baselines.value.certificates = JSON.stringify(certificates.value)
  loaded.value.certificates = true
}

async function loadSection(section: SectionKey) {
  sectionErrors.value[section] = ''
  try {
    if (section === 'delivery') acceptDelivery(await api<DeliverySettings>('/settings/delivery'))
    else if (section === 'smtp') acceptSmtp(await api<SmtpSettings>('/settings/smtp'))
    else if (section === 'mailboxes') acceptMailboxes(await api<MailboxSettings>('/settings/mailboxes'))
    else acceptCertificates(await api<CertificateSettings>('/settings/certificates'))
  } catch (cause) {
    loaded.value[section] = false
    sectionErrors.value[section] = cause instanceof Error ? cause.message : `${sections.find(item => item.key === section)?.title}加载失败`
  }
}

async function loadAll() {
  loading.value = true
  notice.value = ''
  try {
    await Promise.all(sections.map(section => loadSection(section.key)))
  } finally {
    loading.value = false
  }
}

function requestReload() {
  if (dirtyCount.value) reloadConfirmOpen.value = true
  else void loadAll()
}

async function discardAndReload() {
  reloadConfirmOpen.value = false
  await loadAll()
}

function resetCurrent() {
  const baseline = baselines.value[activeSection.value]
  if (!baseline) return
  if (activeSection.value === 'delivery') delivery.value = JSON.parse(baseline) as DeliverySettings
  else if (activeSection.value === 'smtp') smtp.value = JSON.parse(baseline) as SmtpSettings
  else if (activeSection.value === 'mailboxes') mailboxes.value = JSON.parse(baseline) as MailboxSettings
  else certificates.value = JSON.parse(baseline) as CertificateSettings
  folderError.value = ''
  notice.value = ''
}

async function saveCurrent() {
  const section = activeSection.value
  if (!currentDirty.value || !currentValid.value || savingSection.value) return
  savingSection.value = section
  sectionErrors.value[section] = ''
  notice.value = ''
  try {
    if (section === 'delivery') {
      acceptDelivery(await api<DeliverySettings>('/settings/delivery', { method: 'PUT', body: jsonBody(delivery.value) }))
    } else if (section === 'smtp') {
      acceptSmtp(await api<SmtpSettings>('/settings/smtp', { method: 'PUT', body: jsonBody(smtp.value) }))
    } else if (section === 'mailboxes') {
      acceptMailboxes(await api<MailboxSettings>('/settings/mailboxes', { method: 'PUT', body: jsonBody(mailboxes.value) }))
    } else {
      const payload = {
        defaultMailHostName: normalizeHostName(certificates.value.defaultMailHostName) || null,
        allowSingleCertificateFallback: certificates.value.allowSingleCertificateFallback,
      }
      acceptCertificates(await api<CertificateSettings>('/settings/certificates', { method: 'PUT', body: jsonBody(payload) }))
    }
    notice.value = `${currentSection.value.title}已保存`
  } catch (cause) {
    sectionErrors.value[section] = cause instanceof ApiError || cause instanceof Error ? cause.message : `${currentSection.value.title}保存失败`
  } finally {
    savingSection.value = null
  }
}

function addFolder() {
  folderError.value = ''
  const normalized = newFolder.value.trim().replace(/^"|"$/g, '')
  if (!normalized || normalized.includes('..') || normalized.length > 255) {
    folderError.value = '文件夹名称不能为空、不能包含“..”，且不能超过 255 个字符。'
    return
  }
  if (mailboxes.value.defaultFolders.some(folder => folder.toLowerCase() === normalized.toLowerCase())) {
    folderError.value = '该默认文件夹已经存在。'
    return
  }
  mailboxes.value.defaultFolders.push(normalized)
  newFolder.value = ''
}

function removeFolder(folder: string) {
  if (isSystemFolder(folder)) return
  mailboxes.value.defaultFolders = mailboxes.value.defaultFolders.filter(item => item !== folder)
}

function switchSection(section: SectionKey) {
  activeSection.value = section
  notice.value = ''
  folderError.value = ''
}

onBeforeRouteLeave(() => !dirtyCount.value || globalThis.confirm(`有 ${dirtyCount.value} 个设置分区尚未保存，确定离开？`))
onMounted(loadAll)
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/system">系统</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>全局设置</span>
  </div>

  <PageHeader title="全局设置" description="管理直接影响投递、SMTP、邮箱初始化与证书选择的运行参数">
    <v-chip v-if="dirtyCount" color="warning" variant="tonal" size="small">{{ dirtyCount }} 个分区未保存</v-chip>
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="loading" @click="requestReload">重新加载</v-btn>
  </PageHeader>

  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>

  <div class="settings-layout">
    <nav class="settings-nav" aria-label="全局设置分区">
      <button v-for="section in sections" :key="section.key" :class="{ active: activeSection === section.key }" @click="switchSection(section.key)">
        <v-icon size="20">{{ section.icon }}</v-icon>
        <span><b>{{ section.title }}</b><small>{{ section.description }}</small></span>
        <i v-if="sectionErrors[section.key]" class="status-dot is-error" :title="`${section.title}加载失败`" />
        <i v-else-if="dirtySections.includes(section.key)" class="status-dot is-dirty" :title="`${section.title}有未保存修改`" />
        <v-icon v-else size="16" color="success">mdi-check-circle-outline</v-icon>
      </button>
      <div class="nav-note"><v-icon size="17">mdi-information-outline</v-icon><span>保存仅作用于当前分区，不会重启 SMTP、IMAP 或 Worker 进程。</span></div>
    </nav>

    <v-card flat class="panel settings-panel">
      <div class="section-header">
        <div><span>全局设置</span><h2>{{ currentSection.title }}</h2><p>{{ currentSection.description }}</p></div>
        <StatusPill v-if="currentError" label="加载失败" tone="danger" />
        <StatusPill v-else-if="currentDirty" label="未保存" tone="warning" />
        <StatusPill v-else label="已保存" tone="success" />
      </div>

      <v-progress-linear v-if="loading && !currentLoaded" indeterminate color="primary" />
      <div v-if="currentError" class="section-error">
        <v-alert type="error" variant="tonal">{{ currentError }}</v-alert>
        <v-btn class="mt-4" variant="outlined" prepend-icon="mdi-refresh" @click="loadSection(activeSection)">重试当前分区</v-btn>
      </div>

      <div v-else-if="currentLoaded" class="section-body">
        <template v-if="activeSection === 'delivery'">
          <v-alert type="info" variant="tonal" density="compact" class="mb-5">这些参数控制 Worker 获取投递任务的节奏。租约应显著长于单批最慢投递时间，避免同一任务被重复领取。</v-alert>
          <div class="field-grid two-columns">
            <div class="field-block"><v-text-field v-model.number="delivery.batchSize" label="每批投递数量" type="number" min="1" max="200" suffix="封" :error-messages="deliveryErrors.batchSize" hide-details="auto" /><p>单个 Worker 每次领取的最大任务数。</p></div>
            <div class="field-block"><v-text-field v-model.number="delivery.leaseSeconds" label="租约时长" type="number" min="10" max="3600" suffix="秒" :error-messages="deliveryErrors.leaseSeconds" hide-details="auto" /><p>任务处理期间的独占时间，超时后可由其他节点重新领取。</p></div>
            <div class="field-block"><v-text-field v-model.number="delivery.maximumAttempts" label="最大尝试次数" type="number" min="1" max="100" suffix="次" :error-messages="deliveryErrors.maximumAttempts" hide-details="auto" /><p>达到上限后生成退信并停止自动重试。</p></div>
            <div class="field-block"><v-text-field v-model.number="delivery.pollIntervalSeconds" label="空队列轮询间隔" type="number" min="1" max="300" suffix="秒" :error-messages="deliveryErrors.pollIntervalSeconds" hide-details="auto" /><p>没有可投递任务时再次检查队列的等待时间。</p></div>
          </div>
        </template>

        <template v-else-if="activeSection === 'smtp'">
          <v-alert type="info" variant="tonal" density="compact" class="mb-5">命令超时适用于 SMTP 会话各命令阶段。设置过短会中断慢速但合法的客户端，设置过长会占用连接资源。</v-alert>
          <div class="field-grid single-column">
            <div class="field-block"><v-text-field v-model.number="smtp.commandTimeoutSeconds" label="SMTP 命令超时" type="number" min="5" max="3600" suffix="秒" :error-messages="smtpError" hide-details="auto" /><p>允许范围 5 秒至 1 小时，建议生产环境保持 300 秒。</p></div>
          </div>
        </template>

        <template v-else-if="activeSection === 'mailboxes'">
          <v-alert type="info" variant="tonal" density="compact" class="mb-5">仅影响以后创建的新邮箱。6 个系统文件夹由服务器强制创建，不能在此删除；已有邮箱不会被批量修改。</v-alert>
          <div class="folder-add">
            <v-text-field v-model="newFolder" label="新增自定义文件夹" placeholder="例如 Projects" :error-messages="folderError" hide-details="auto" maxlength="255" @keyup.enter="addFolder" />
            <v-btn color="primary" variant="tonal" prepend-icon="mdi-folder-plus-outline" :disabled="!newFolder.trim()" @click="addFolder">添加文件夹</v-btn>
          </div>
          <div class="folder-list">
            <div v-for="folder in mailboxes.defaultFolders" :key="folder.toLowerCase()" class="folder-row">
              <v-icon :color="isSystemFolder(folder) ? 'primary' : undefined">{{ isSystemFolder(folder) ? 'mdi-folder-lock-outline' : 'mdi-folder-outline' }}</v-icon>
              <div><b>{{ folder }}</b><span>{{ isSystemFolder(folder) ? '系统文件夹 · 必须保留' : '自定义默认文件夹' }}</span></div>
              <v-chip v-if="isSystemFolder(folder)" size="x-small" variant="tonal">系统</v-chip>
              <v-btn v-else icon="mdi-delete-outline" variant="text" color="error" :aria-label="`移除默认文件夹 ${folder}`" @click="removeFolder(folder)" />
            </div>
          </div>
          <p class="folder-count">新邮箱将创建 {{ mailboxes.defaultFolders.length }} 个默认文件夹，其中 {{ customFolders.length }} 个为自定义。</p>
        </template>

        <template v-else>
          <v-alert type="info" variant="tonal" density="compact" class="mb-5">域名证书仍优先按邮件主机名精确匹配。这里配置的是无法精确匹配时的全局选择策略。</v-alert>
          <div class="certificate-grid">
            <div>
              <v-combobox v-model="certificates.defaultMailHostName" :items="certificates.availableMailHostNames" label="默认邮件主机名" clearable placeholder="留空表示不指定全局默认证书" :error-messages="certificateHostError" hide-details="auto" />
              <p class="field-help">可选择已发现主机名，也可输入将来准备配置的主机名。</p>
              <v-switch v-model="certificates.allowSingleCertificateFallback" color="primary" label="仅有一张可用证书时自动作为后备" hide-details />
            </div>
            <div class="certificate-status">
              <span>按当前设置预估</span>
              <div v-if="draftEffectiveCertificate" class="effective is-success"><v-icon>mdi-certificate</v-icon><div><b>{{ draftEffectiveCertificate }}</b><small>将作为全局有效默认证书</small></div></div>
              <div v-else-if="configuredCertificateMissing" class="effective is-warning"><v-icon>mdi-certificate-off-outline</v-icon><div><b>配置的主机名暂无可用证书</b><small>保存不会失败，但全局默认证书暂时不生效</small></div></div>
              <div v-else class="effective"><v-icon>mdi-shield-key-outline</v-icon><div><b>不使用全局默认证书</b><small>继续按连接目标主机名精确选择证书</small></div></div>
              <div class="available-hosts"><b>当前可用主机名</b><v-chip v-for="host in certificates.availableMailHostNames" :key="host" size="small" variant="tonal">{{ host }}</v-chip><span v-if="!certificates.availableMailHostNames.length">尚未发现可用域证书</span></div>
            </div>
          </div>
        </template>
      </div>

      <div v-if="currentLoaded && !currentError" class="section-actions">
        <span v-if="currentDirty">当前分区有未保存修改</span><span v-else>当前分区已与服务器同步</span>
        <v-spacer />
        <v-btn variant="text" :disabled="!currentDirty || Boolean(savingSection)" @click="resetCurrent">撤销当前修改</v-btn>
        <v-btn v-if="can('settings.manage')" color="primary" :loading="savingSection === activeSection" :disabled="!currentDirty || !currentValid || Boolean(savingSection)" @click="saveCurrent">保存当前设置</v-btn>
      </div>
    </v-card>
  </div>

  <v-dialog v-model="reloadConfirmOpen" max-width="500" persistent>
    <v-card>
      <v-card-title>放弃所有未保存修改？</v-card-title>
      <v-card-text>重新加载会覆盖 {{ dirtyCount }} 个分区中的本地修改，并重新读取服务器设置。</v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" @click="reloadConfirmOpen = false">继续编辑</v-btn><v-btn color="error" variant="tonal" @click="discardAndReload">放弃并重新加载</v-btn></v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.settings-layout { display: grid; grid-template-columns: 260px minmax(0, 1fr); align-items: start; gap: 16px; }
.settings-nav { position: sticky; top: 76px; overflow: hidden; border: 1px solid var(--border); border-radius: 10px; background: #fff; }
.settings-nav button { display: grid; grid-template-columns: 25px minmax(0, 1fr) 18px; align-items: center; gap: 10px; width: 100%; padding: 14px 15px; border: 0; border-bottom: 1px solid var(--border); color: var(--text-secondary); background: transparent; text-align: left; cursor: pointer; }
.settings-nav button:hover { background: #f8fafc; }
.settings-nav button.active { color: #175cb8; background: #f2f7ff; box-shadow: inset 3px 0 #2c72d6; }
.settings-nav button span { display: flex; flex-direction: column; gap: 2px; }
.settings-nav button b { font-size: 13px; }
.settings-nav button small { color: var(--text-muted); font-size: 11px; }
.status-dot { width: 8px; height: 8px; border-radius: 50%; }
.status-dot.is-dirty { background: #e9a23b; box-shadow: 0 0 0 4px #fff4de; }
.status-dot.is-error { background: #d94a5c; box-shadow: 0 0 0 4px #ffedf0; }
.nav-note { display: flex; align-items: flex-start; gap: 8px; padding: 14px; color: var(--text-muted); background: #f8fafc; font-size: 11px; line-height: 1.55; }
.settings-panel { overflow: hidden; }
.section-header { display: flex; align-items: center; justify-content: space-between; gap: 24px; padding: 20px 22px; border-bottom: 1px solid var(--border); }
.section-header > div { min-width: 0; }
.section-header span { color: var(--text-muted); font-size: 10px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
.section-header h2 { margin: 2px 0; font-size: 19px; }
.section-header p { margin: 0; color: var(--text-muted); font-size: 12px; }
.section-error, .section-body { padding: 24px; }
.field-grid { display: grid; gap: 20px 28px; max-width: 900px; }
.field-grid.two-columns { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.field-grid.single-column { grid-template-columns: minmax(280px, 520px); }
.field-block p, .field-help { margin: 7px 4px 0; color: var(--text-muted); font-size: 11px; line-height: 1.5; }
.folder-add { display: grid; grid-template-columns: minmax(280px, 560px) auto; align-items: start; gap: 10px; max-width: 760px; margin-bottom: 18px; }
.folder-add .v-btn { margin-top: 2px; }
.folder-list { overflow: hidden; max-width: 760px; border: 1px solid var(--border); border-radius: 8px; }
.folder-row { display: grid; grid-template-columns: 28px minmax(0, 1fr) auto; align-items: center; gap: 10px; min-height: 58px; padding: 8px 12px; border-bottom: 1px solid var(--border); }
.folder-row:last-child { border-bottom: 0; }
.folder-row > div { display: flex; flex-direction: column; gap: 2px; }
.folder-row b { font-size: 13px; }
.folder-row span { color: var(--text-muted); font-size: 11px; }
.folder-count { margin: 12px 2px 0; color: var(--text-muted); font-size: 11px; }
.certificate-grid { display: grid; grid-template-columns: minmax(300px, .9fr) minmax(340px, 1.1fr); gap: 32px; }
.certificate-status { padding: 16px; border: 1px solid var(--border); border-radius: 9px; background: #f8fafc; }
.certificate-status > span { color: var(--text-muted); font-size: 10px; font-weight: 700; letter-spacing: .05em; }
.effective { display: flex; align-items: center; gap: 12px; margin: 10px 0 18px; padding: 13px; border: 1px solid var(--border); border-radius: 8px; background: #fff; }
.effective > div { display: flex; flex-direction: column; gap: 2px; }
.effective b { font-size: 13px; }
.effective small { color: var(--text-muted); font-size: 11px; }
.effective.is-success { border-color: #bfe8d7; color: #0d7750; background: #f2fbf7; }
.effective.is-warning { border-color: #f0d8a8; color: #9a5e00; background: #fff9ed; }
.available-hosts { display: flex; align-items: center; flex-wrap: wrap; gap: 7px; }
.available-hosts b { width: 100%; margin-bottom: 2px; color: var(--text-secondary); font-size: 11px; }
.available-hosts > span { color: var(--text-muted); font-size: 11px; }
.section-actions { display: flex; align-items: center; min-height: 68px; padding: 12px 22px; border-top: 1px solid var(--border); background: #fbfcfe; }
.section-actions > span { color: var(--text-muted); font-size: 11px; }
@media (max-width: 1050px) {
  .settings-layout { grid-template-columns: 1fr; }
  .settings-nav { position: static; display: grid; grid-template-columns: repeat(2, 1fr); }
  .settings-nav button { border-right: 1px solid var(--border); }
  .nav-note { grid-column: 1 / -1; }
  .certificate-grid { grid-template-columns: 1fr; }
}
@media (max-width: 700px) {
  .settings-nav, .field-grid.two-columns { grid-template-columns: 1fr; }
  .folder-add { grid-template-columns: 1fr; }
  .section-actions { align-items: stretch; flex-direction: column; gap: 8px; }
}
</style>
