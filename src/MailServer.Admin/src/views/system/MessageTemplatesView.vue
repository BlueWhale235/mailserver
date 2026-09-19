<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
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
}

type TemplateKind = 'permanent' | 'retry'

const { can } = usePermission()
const settings = ref<TemplateSettings>({
  senderDisplayName: '',
  permanentFailureSubject: '',
  permanentFailureBody: '',
  retryExhaustedSubject: '',
  retryExhaustedBody: '',
})
const baseline = ref('')
const ready = ref(false)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const notice = ref('')
const activeTemplate = ref<TemplateKind>('permanent')

const placeholders = [
  { token: '{recipient}', label: '收件人', sample: 'customer@example.net' },
  { token: '{reason}', label: '失败原因', sample: '550 5.1.1 收件人不存在' },
  { token: '{queueId}', label: '队列编号', sample: '8f5d1b62-0b36-42d8-9bb5-1a81d9d12e40' },
  { token: '{attempts}', label: '尝试次数', sample: '5' },
]

const dirty = computed(() => ready.value && JSON.stringify(settings.value) !== baseline.value)
const senderError = computed(() => lengthError(settings.value.senderDisplayName, 120, '发件人显示名称'))
const permanentSubjectError = computed(() => lengthError(settings.value.permanentFailureSubject, 200, '永久失败主题'))
const retrySubjectError = computed(() => lengthError(settings.value.retryExhaustedSubject, 200, '重试耗尽主题'))
const permanentBodyError = computed(() => lengthError(settings.value.permanentFailureBody, 8000, '永久失败正文'))
const retryBodyError = computed(() => lengthError(settings.value.retryExhaustedBody, 8000, '重试耗尽正文'))
const valid = computed(() => !senderError.value && !permanentSubjectError.value && !retrySubjectError.value && !permanentBodyError.value && !retryBodyError.value)

const currentSubject = computed({
  get: () => activeTemplate.value === 'permanent' ? settings.value.permanentFailureSubject : settings.value.retryExhaustedSubject,
  set: value => {
    if (activeTemplate.value === 'permanent') settings.value.permanentFailureSubject = value
    else settings.value.retryExhaustedSubject = value
  },
})

const currentBody = computed({
  get: () => activeTemplate.value === 'permanent' ? settings.value.permanentFailureBody : settings.value.retryExhaustedBody,
  set: value => {
    if (activeTemplate.value === 'permanent') settings.value.permanentFailureBody = value
    else settings.value.retryExhaustedBody = value
  },
})

const currentSubjectError = computed(() => activeTemplate.value === 'permanent' ? permanentSubjectError.value : retrySubjectError.value)
const currentBodyError = computed(() => activeTemplate.value === 'permanent' ? permanentBodyError.value : retryBodyError.value)
const renderedSubject = computed(() => renderText(currentSubject.value))
const renderedBody = computed(() => renderHtml(currentBody.value))
const previewDocument = computed(() => `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:"><style>body{margin:0;padding:28px;color:#25364a;background:#fff;font:14px/1.7 system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}a{color:#2563eb}table{max-width:100%;border-collapse:collapse}img{max-width:100%}code{padding:2px 5px;border-radius:4px;background:#f1f5f9}</style></head><body>${renderedBody.value}</body></html>`)
const unknownTokens = computed(() => {
  const matches = `${settings.value.permanentFailureSubject} ${settings.value.permanentFailureBody} ${settings.value.retryExhaustedSubject} ${settings.value.retryExhaustedBody}`.match(/\{[a-zA-Z][a-zA-Z0-9]*\}/g) ?? []
  const known = new Set(placeholders.map(item => item.token.toLowerCase()))
  return Array.from(new Set(matches.filter(token => !known.has(token.toLowerCase()))))
})
const riskyMarkup = computed(() => /<script\b|\son\w+\s*=|javascript\s*:/i.test(`${settings.value.permanentFailureBody} ${settings.value.retryExhaustedBody}`))

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
  for (const placeholder of placeholders) {
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
}

function insertPlaceholder(token: string) {
  currentBody.value = `${currentBody.value}${currentBody.value.endsWith(' ') || !currentBody.value ? '' : ' '}${token}`
}

async function save() {
  if (!dirty.value || !valid.value || saving.value) return
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

onBeforeRouteLeave(() => !dirty.value || globalThis.confirm('消息模板尚未保存，确定离开？'))
onMounted(load)
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/system">系统</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>消息模板</span>
  </div>

  <PageHeader title="消息模板" description="编辑永久失败与重试耗尽时发送给原发件人的退信">
    <v-btn variant="outlined" prepend-icon="mdi-undo" :disabled="!dirty || saving" @click="reset">撤销修改</v-btn>
    <v-btn v-if="can('settings.manage')" color="primary" prepend-icon="mdi-content-save-outline" :loading="saving" :disabled="!dirty || !valid" @click="save">保存模板</v-btn>
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
    </div>

    <v-alert v-if="unknownTokens.length" type="warning" variant="tonal" density="compact" class="mb-4">
      未识别的占位符不会被替换：{{ unknownTokens.join('、') }}
    </v-alert>
    <v-alert v-if="riskyMarkup" type="warning" variant="tonal" density="compact" class="mb-4">
      正文包含脚本、事件处理器或 javascript 链接。预览沙箱不会执行这些内容，实际邮件客户端的处理方式可能不同，建议移除。
    </v-alert>

    <div class="editor-layout">
      <v-card flat class="panel editor-panel">
        <div class="panel-heading"><div><h2>编辑内容</h2><p>HTML 正文中的占位符值会在服务端进行转义。</p></div><v-chip size="small" variant="tonal">{{ activeTemplate === 'permanent' ? '永久失败' : '重试耗尽' }}</v-chip></div>
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
        <div class="panel-heading"><div><h2>实时预览</h2><p>使用示例值替换占位符，并在无脚本沙箱中渲染。</p></div><v-icon color="primary">mdi-eye-outline</v-icon></div>
        <div class="mail-preview">
          <div class="mail-header">
            <dl><div><dt>发件人</dt><dd>{{ settings.senderDisplayName || '—' }}</dd></div><div><dt>收件人</dt><dd>original-sender@northwind.cn</dd></div><div><dt>主题</dt><dd>{{ renderedSubject || '—' }}</dd></div></dl>
          </div>
          <iframe title="退信 HTML 实时预览" sandbox="" :srcdoc="previewDocument" />
        </div>
        <div class="sample-values"><b>预览示例值</b><span v-for="placeholder in placeholders" :key="placeholder.token"><code>{{ placeholder.token }}</code> {{ placeholder.sample }}</span></div>
      </v-card>
    </div>
  </template>
</template>

<style scoped>
.sender-panel { display: grid; grid-template-columns: minmax(260px, .7fr) minmax(420px, 1.3fr); align-items: center; gap: 38px; padding: 20px; }
.sender-panel h2, .panel-heading h2 { margin: 0 0 3px; font-size: 16px; }
.sender-panel p, .panel-heading p { margin: 0; color: var(--text-muted); font-size: 12px; }
.template-tabs { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; margin-bottom: 16px; }
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
