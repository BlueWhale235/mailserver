<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import PageHeader from '../../components/PageHeader.vue'
import { ApiError, api, jsonBody } from '../../lib/api'
import { usePermission } from '../../composables/usePermission'

interface TemplateSettings {
  senderDisplayName: string
  permanentFailureSubject: string
  permanentFailureBody: string
  retryExhaustedSubject: string
  retryExhaustedBody: string
  quotaExceededSubject: string
  quotaExceededBody: string
}

type TemplateKind = 'permanent' | 'retry' | 'quota'

const { can } = usePermission()
const settings = ref<TemplateSettings>({
  senderDisplayName: '',
  permanentFailureSubject: '',
  permanentFailureBody: '',
  retryExhaustedSubject: '',
  retryExhaustedBody: '',
  quotaExceededSubject: '',
  quotaExceededBody: '',
})
const baseline = ref('')
const ready = ref(false)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const notice = ref('')
const activeTemplate = ref<TemplateKind>('permanent')
const resetConfirmOpen = ref(false)
const defaultsConfirmOpen = ref(false)
const leaveConfirmOpen = ref(false)
const previewBusy = ref(false)
const previewError = ref('')
const serverPreview = ref<{ key: string; renderedSubject: string; renderedHtml: string } | null>(null)
let resolvePendingLeave: ((allow: boolean) => void) | null = null
let previewVersion = 0

const placeholders = computed(() => [
  { token: '{recipient}', label: '收件人', sample: 'customer@example.net' },
  { token: '{reason}', label: '失败原因', sample: activeTemplate.value === 'quota' ? '552 5.2.2 收件方容量超限，请手动联系收件人' : '550 5.1.1 收件人不存在' },
  { token: '{queueId}', label: '队列编号', sample: '8f5d1b62-0b36-42d8-9bb5-1a81d9d12e40' },
  { token: '{attempts}', label: '尝试次数', sample: '5' },
])

const dirty = computed(() => ready.value && JSON.stringify(settings.value) !== baseline.value)
const senderError = computed(() => lengthError(settings.value.senderDisplayName, 120, '发件人显示名称'))
const permanentSubjectError = computed(() => lengthError(settings.value.permanentFailureSubject, 200, '永久失败主题'))
const retrySubjectError = computed(() => lengthError(settings.value.retryExhaustedSubject, 200, '重试耗尽主题'))
const permanentBodyError = computed(() => lengthError(settings.value.permanentFailureBody, 8000, '永久失败正文'))
const retryBodyError = computed(() => lengthError(settings.value.retryExhaustedBody, 8000, '重试耗尽正文'))
const quotaSubjectError = computed(() => lengthError(settings.value.quotaExceededSubject, 200, '超限退信主题'))
const quotaBodyError = computed(() => lengthError(settings.value.quotaExceededBody, 8000, '超限退信正文'))
const valid = computed(() => !senderError.value && !permanentSubjectError.value && !retrySubjectError.value && !permanentBodyError.value && !retryBodyError.value && !quotaSubjectError.value && !quotaBodyError.value)
const templateFields = {
  permanent: { subject: 'permanentFailureSubject', body: 'permanentFailureBody', label: '永久失败' },
  retry: { subject: 'retryExhaustedSubject', body: 'retryExhaustedBody', label: '重试耗尽' },
  quota: { subject: 'quotaExceededSubject', body: 'quotaExceededBody', label: '超限退信' },
} as const
const currentSubject = computed({
  get: () => settings.value[templateFields[activeTemplate.value].subject],
  set: value => { settings.value[templateFields[activeTemplate.value].subject] = value },
})
const currentBody = computed({
  get: () => settings.value[templateFields[activeTemplate.value].body],
  set: value => { settings.value[templateFields[activeTemplate.value].body] = value },
})
const currentSubjectError = computed(() => ({ permanent: permanentSubjectError.value, retry: retrySubjectError.value, quota: quotaSubjectError.value })[activeTemplate.value])
const currentBodyError = computed(() => ({ permanent: permanentBodyError.value, retry: retryBodyError.value, quota: quotaBodyError.value })[activeTemplate.value])
const renderedSubject = computed(() => renderText(currentSubject.value))
const renderedBody = computed(() => renderHtml(currentBody.value))
const previewKey = computed(() => JSON.stringify([activeTemplate.value, currentSubject.value, currentBody.value]))
const previewVerified = computed(() => serverPreview.value?.key === previewKey.value)
const previewSubject = computed(() => previewVerified.value ? serverPreview.value!.renderedSubject : renderedSubject.value)
const previewBody = computed(() => previewVerified.value ? serverPreview.value!.renderedHtml : renderedBody.value)
const previewDocument = computed(() => `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:"><style>body{margin:0;padding:28px;color:#25364a;background:#fff;font:14px/1.7 system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}a{color:#2563eb}table{max-width:100%;border-collapse:collapse}img{max-width:100%}code{padding:2px 5px;border-radius:4px;background:#f1f5f9}</style></head><body>${previewBody.value}</body></html>`)
const unknownTokens = computed(() => {
  const matches = `${settings.value.permanentFailureSubject} ${settings.value.permanentFailureBody} ${settings.value.retryExhaustedSubject} ${settings.value.retryExhaustedBody} ${settings.value.quotaExceededSubject} ${settings.value.quotaExceededBody}`.match(/\{[a-zA-Z][a-zA-Z0-9]*\}/g) ?? []
  const known = new Set(placeholders.value.map(item => item.token.toLowerCase()))
  return Array.from(new Set(matches.filter(token => !known.has(token.toLowerCase()))))
})
const riskyMarkup = computed(() => /<script\b|\son\w+\s*=|javascript\s*:/i.test(`${settings.value.permanentFailureBody} ${settings.value.retryExhaustedBody} ${settings.value.quotaExceededBody}`))

function lengthError(value: string, maximum: number, label: string) {
  const length = value.trim().length
  if (length < 1) return `${label}不能为空`
  if (length > maximum) return `${label}不能超过 ${maximum} 个字符`
  return ''
}

function htmlEncode(value: string) {
  return value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;')
}

function replaceTokens(value: string, encode: boolean) {
  let result = value
  for (const placeholder of placeholders.value) {
    const replacement = encode ? htmlEncode(placeholder.sample) : placeholder.sample
    const expression = new RegExp(`\\${placeholder.token[0]}${placeholder.token.slice(1, -1)}\\}`, 'gi')
    result = result.replace(expression, replacement)
  }
  return result
}

function renderText(value: string) {
  return replaceTokens(value, false)
}

function renderHtml(value: string) {
  return replaceTokens(value, true)
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const result = await api<TemplateSettings>('/settings/message-templates')
    settings.value = result
    baseline.value = JSON.stringify(result)
    ready.value = true
  } catch (cause) {
    ready.value = false
    error.value = cause instanceof Error ? cause.message : '消息模板加载失败'
  } finally {
    loading.value = false
  }
}

function reset() {
  if (!baseline.value) return
  settings.value = JSON.parse(baseline.value) as TemplateSettings
  error.value = ''
  notice.value = ''
  resetConfirmOpen.value = false
  serverPreview.value = null
}

async function restoreDefaults() {
  if (saving.value || loading.value) return
  defaultsConfirmOpen.value = false
  error.value = ''
  try {
    settings.value = await api<TemplateSettings>('/settings/message-templates/defaults')
    serverPreview.value = null
    notice.value = '已载入系统默认模板，检查后点击“保存模板”才会生效。'
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '加载系统默认模板失败'
  }
}

async function verifyPreview() {
  if (previewBusy.value || saving.value || !valid.value) return
  const key = previewKey.value
  const version = ++previewVersion
  previewBusy.value = true
  previewError.value = ''
  try {
    const result = await api<{ renderedSubject: string; renderedHtml: string }>('/settings/message-templates/preview', {
      method: 'POST', body: jsonBody({ subject: currentSubject.value, body: currentBody.value, templateKind: activeTemplate.value }),
    })
    if (version === previewVersion && key === previewKey.value) serverPreview.value = { key, ...result }
  } catch (cause) {
    if (version === previewVersion && key === previewKey.value)
      previewError.value = cause instanceof Error ? cause.message : '服务端预览失败'
  } finally {
    if (version === previewVersion) previewBusy.value = false
  }
}

function insertPlaceholder(token: string) {
  currentBody.value = `${currentBody.value}${currentBody.value.endsWith(' ') || !currentBody.value ? '' : ' '}${token}`
}

async function save() {
  if (!dirty.value || !valid.value || saving.value || riskyMarkup.value) return
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    const saved = await api<TemplateSettings>('/settings/message-templates', {
      method: 'PUT',
      body: jsonBody(settings.value),
    })
    settings.value = saved
    baseline.value = JSON.stringify(saved)
    notice.value = '退信消息模板已保存'
  } catch (cause) {
    error.value = cause instanceof ApiError || cause instanceof Error ? cause.message : '消息模板保存失败'
  } finally {
    saving.value = false
  }
}

function protectPageExit(event: BeforeUnloadEvent) {
  if (!dirty.value && !saving.value) return
  event.preventDefault()
  event.returnValue = ''
}

function finishLeave(allow: boolean) {
  leaveConfirmOpen.value = false
  const resolve = resolvePendingLeave
  resolvePendingLeave = null
  resolve?.(allow)
}

onBeforeRouteLeave(() => {
  if (saving.value) {
    error.value = '模板正在保存，请等待操作完成后再离开。'
    return false
  }
  if (!dirty.value) return true
  resolvePendingLeave?.(false)
  leaveConfirmOpen.value = true
  return new Promise<boolean>(resolve => { resolvePendingLeave = resolve })
})
watch(previewKey, () => { previewError.value = '' })
onMounted(() => {
  globalThis.addEventListener('beforeunload', protectPageExit)
  void load()
})
onBeforeUnmount(() => {
  globalThis.removeEventListener('beforeunload', protectPageExit)
  resolvePendingLeave?.(false)
  resolvePendingLeave = null
  ++previewVersion
})
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/system">系统</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>消息模板</span>
  </div>

  <PageHeader title="消息模板" description="编辑永久失败、重试耗尽及容量超限时发送给原发件人的退信">
    <v-btn v-if="can('settings.manage')" variant="outlined" prepend-icon="mdi-restore" :disabled="saving || loading" @click="defaultsConfirmOpen = true">恢复系统默认值</v-btn>
    <v-btn v-if="can('settings.manage')" variant="outlined" prepend-icon="mdi-undo" :disabled="!dirty || saving" @click="resetConfirmOpen = true">撤销修改</v-btn>
    <v-btn v-if="can('settings.manage')" color="primary" prepend-icon="mdi-content-save-outline" :loading="saving" :disabled="!dirty || !valid || riskyMarkup" @click="save">保存模板</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}<v-btn v-if="!ready" variant="text" @click="load">重试</v-btn></v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>
  <v-progress-linear v-if="loading" indeterminate color="primary" class="mb-4" />

  <template v-if="ready">
    <v-card flat class="panel sender-panel mb-4">
      <div><h2>发件人身份</h2><p>作为退信邮件的显示名称，不改变系统退信地址。</p></div>
      <v-text-field v-model="settings.senderDisplayName" label="发件人显示名称" counter="120" :error-messages="senderError" :readonly="saving || !can('settings.manage')" hide-details="auto" />
    </v-card>

    <div class="template-tabs" role="tablist" aria-label="退信模板类型">
      <button :class="{ active: activeTemplate === 'permanent' }" role="tab" :aria-selected="activeTemplate === 'permanent'" @click="activeTemplate = 'permanent'">
        <v-icon size="19">mdi-email-remove-outline</v-icon><span><b>永久失败</b><small>远端明确拒收，立即生成退信</small></span>
      </button>
      <button :class="{ active: activeTemplate === 'retry' }" role="tab" :aria-selected="activeTemplate === 'retry'" @click="activeTemplate = 'retry'">
        <v-icon size="19">mdi-email-clock-outline</v-icon><span><b>重试耗尽</b><small>达到最大尝试次数后生成退信</small></span>
      </button>
      <button :class="{ active: activeTemplate === 'quota' }" role="tab" :aria-selected="activeTemplate === 'quota'" @click="activeTemplate = 'quota'">
        <v-icon size="19">mdi-email-alert-outline</v-icon><span><b>超限退信</b><small>收件方容量不足，提示手动联系收件人</small></span>
      </button>
    </div>

    <v-alert v-if="unknownTokens.length" type="warning" variant="tonal" density="compact" class="mb-4">
      未识别的占位符不会被替换：{{ unknownTokens.join('、') }}
    </v-alert>
    <v-alert v-if="riskyMarkup" type="warning" variant="tonal" density="compact" class="mb-4">
      正文包含脚本、事件处理器或危险链接；服务端不会保存。请移除后再保存。
    </v-alert>
    <v-alert type="info" variant="tonal" density="compact" class="mb-4">正文支持安全 XHTML：段落、标题、强调、列表、表格、代码及 http／https／mailto 链接。标签必须闭合；仅允许安全的行内样式，不允许图片或脚本。</v-alert>

    <div class="editor-layout">
      <v-card flat class="panel editor-panel">
        <div class="panel-heading"><div><h2>编辑内容</h2><p>HTML 正文中的占位符值会在服务端进行转义。</p></div><v-chip size="small" variant="tonal">{{ templateFields[activeTemplate].label }}</v-chip></div>
        <div class="editor-fields">
          <v-text-field v-model="currentSubject" label="邮件主题" counter="200" :error-messages="currentSubjectError" :readonly="saving || !can('settings.manage')" hide-details="auto" />
          <div class="placeholder-bar">
            <span>插入正文：</span>
            <v-btn v-for="placeholder in placeholders" :key="placeholder.token" size="small" variant="tonal" :disabled="saving || !can('settings.manage')" :aria-label="`插入${placeholder.label}占位符 ${placeholder.token}`" @click="insertPlaceholder(placeholder.token)">
              {{ placeholder.label }} <code>{{ placeholder.token }}</code>
            </v-btn>
          </div>
          <v-textarea v-model="currentBody" label="HTML 正文" rows="15" counter="8000" :error-messages="currentBodyError" :readonly="saving || !can('settings.manage')" hide-details="auto" class="body-editor" />
        </div>
      </v-card>

      <v-card flat class="panel preview-panel">
        <div class="panel-heading"><div><h2>模板预览</h2><p>{{ previewVerified ? '已按真实退信渲染链路通过服务端验证。' : '编辑器示意；点击服务端预览核对实际退信渲染。' }}</p></div><v-btn variant="tonal" size="small" prepend-icon="mdi-server-network" :loading="previewBusy" :disabled="saving || !valid" @click="verifyPreview">服务端预览</v-btn></div>
        <v-alert v-if="previewError" type="error" variant="tonal" density="compact" class="mx-5 mt-4">{{ previewError }}</v-alert>
        <div class="mail-preview">
          <div class="mail-header">
            <dl><div><dt>发件人</dt><dd>{{ settings.senderDisplayName || '—' }}</dd></div><div><dt>收件人</dt><dd>original-sender@northwind.cn</dd></div><div><dt>主题</dt><dd>{{ previewSubject || '—' }}</dd></div></dl>
          </div>
          <iframe title="退信 HTML 实时预览" sandbox="" :srcdoc="previewDocument" />
        </div>
        <div class="sample-values"><b>预览示例值</b><span v-for="placeholder in placeholders" :key="placeholder.token"><code>{{ placeholder.token }}</code> {{ placeholder.sample }}</span></div>
      </v-card>
    </div>
  </template>

  <v-dialog v-model="resetConfirmOpen" max-width="500" persistent>
    <v-card><v-card-title>撤销未保存的模板修改？</v-card-title><v-card-text>将恢复上次从服务器读取的内容，当前草稿无法找回。</v-card-text><v-card-actions><v-spacer /><v-btn variant="text" @click="resetConfirmOpen = false">继续编辑</v-btn><v-btn color="error" variant="tonal" @click="reset">确认撤销</v-btn></v-card-actions></v-card>
  </v-dialog>
  <v-dialog v-model="defaultsConfirmOpen" max-width="500" persistent>
    <v-card><v-card-title>载入系统默认模板？</v-card-title><v-card-text>默认值会替换当前编辑草稿；只有点击“保存模板”后才会写入服务器。</v-card-text><v-card-actions><v-spacer /><v-btn variant="text" @click="defaultsConfirmOpen = false">继续编辑</v-btn><v-btn color="primary" variant="tonal" @click="restoreDefaults">载入默认值</v-btn></v-card-actions></v-card>
  </v-dialog>
  <v-dialog :model-value="leaveConfirmOpen" max-width="500" persistent>
    <v-card><v-card-title>离开并放弃模板修改？</v-card-title><v-card-text>消息模板尚未保存，离开后当前草稿将无法找回。</v-card-text><v-card-actions><v-spacer /><v-btn variant="text" @click="finishLeave(false)">继续编辑</v-btn><v-btn color="error" variant="tonal" @click="finishLeave(true)">放弃并离开</v-btn></v-card-actions></v-card>
  </v-dialog>
</template>

<style scoped>
.sender-panel { display: grid; grid-template-columns: minmax(260px, .7fr) minmax(420px, 1.3fr); align-items: center; gap: 38px; padding: 20px; }
.sender-panel h2, .panel-heading h2 { margin: 0 0 3px; font-size: 16px; }
.sender-panel p, .panel-heading p { margin: 0; color: var(--text-muted); font-size: 12px; }
.template-tabs { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; margin-bottom: 16px; }
.template-tabs button { display: flex; align-items: center; gap: 12px; padding: 14px 16px; border: 1px solid var(--border); border-radius: 9px; color: var(--text-secondary); background: #fff; text-align: left; cursor: pointer; }
.template-tabs button:hover { border-color: #a9bdd6; }
.template-tabs button.active { border-color: #5d8dd6; color: #175cb8; background: #f4f8ff; box-shadow: inset 0 0 0 1px #5d8dd6; }
.template-tabs span { display: flex; flex-direction: column; gap: 2px; }
.template-tabs b { font-size: 13px; }
.template-tabs small { color: var(--text-muted); font-size: 11px; }
.editor-layout { display: grid; grid-template-columns: minmax(480px, 1fr) minmax(480px, 1fr); align-items: start; gap: 16px; }
.panel-heading { display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; padding: 18px 20px; border-bottom: 1px solid var(--border); }
.editor-fields { padding: 20px; }
.placeholder-bar { display: flex; align-items: center; flex-wrap: wrap; gap: 7px; margin: 16px 0; }
.placeholder-bar > span { margin-right: 2px; color: var(--text-muted); font-size: 12px; }
.placeholder-bar code { margin-left: 4px; font-size: 10px; }
.body-editor :deep(textarea) { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 12px; line-height: 1.6; }
.mail-preview { margin: 20px; overflow: hidden; border: 1px solid var(--border); border-radius: 9px; background: #fff; box-shadow: 0 6px 18px rgba(32, 54, 84, .07); }
.mail-header { padding: 13px 16px; border-bottom: 1px solid var(--border); background: #f8fafc; }
.mail-header dl { display: grid; gap: 5px; margin: 0; }
.mail-header dl div { display: grid; grid-template-columns: 58px minmax(0, 1fr); gap: 8px; }
.mail-header dt { color: var(--text-muted); font-size: 11px; }
.mail-header dd { margin: 0; overflow: hidden; color: var(--text-secondary); font-size: 12px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.mail-preview iframe { display: block; width: 100%; height: 335px; border: 0; background: #fff; }
.sample-values { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 6px 16px; margin: 0 20px 20px; padding: 13px 15px; border-radius: 8px; color: var(--text-muted); background: #f8fafc; font-size: 11px; }
.sample-values b { grid-column: 1 / -1; color: var(--text-secondary); }
.sample-values span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
@media (max-width: 1400px) {
  .editor-layout { grid-template-columns: 1fr; }
}
@media (max-width: 760px) {
  .sender-panel, .template-tabs { grid-template-columns: 1fr; }
  .sample-values { grid-template-columns: 1fr; }
}
</style>
