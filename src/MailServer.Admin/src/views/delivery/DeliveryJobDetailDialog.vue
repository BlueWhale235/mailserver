<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { api } from '../../lib/api'
import StatusPill from '../../components/StatusPill.vue'
import { deliveryStatusPresentation, formatDate, shortId, stageLabels } from './deliveryPresentation'
import type { DeliveryAttempt, DeliveryJob, DeliveryRawMessage } from './types'

const props = defineProps<{
  modelValue: boolean
  job: DeliveryJob | null
  domainId?: string
}>()

const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()

const tab = ref<'attempts' | 'message'>('attempts')
const attempts = ref<DeliveryAttempt[]>([])
const raw = ref<DeliveryRawMessage | null>(null)
const attemptsLoading = ref(false)
const rawLoading = ref(false)
const error = ref('')
const attemptsError = ref(false)
let requestVersion = 0

const status = computed(() => props.job ? deliveryStatusPresentation(props.job.status) : null)

async function loadAttempts() {
  if (!props.job) return
  const version = requestVersion
  attemptsLoading.value = true
  attemptsError.value = false
  error.value = ''
  try {
    const result = await api<DeliveryAttempt[]>(queuePath('/' + props.job.id + '/attempts'))
    if (version === requestVersion) attempts.value = result
  }
  catch (cause) { if (version === requestVersion) { attemptsError.value = true; error.value = cause instanceof Error ? cause.message : '投递尝试加载失败' } }
  finally { if (version === requestVersion) attemptsLoading.value = false }
}

async function loadRaw() {
  if (!props.job || raw.value || rawLoading.value) return
  const version = requestVersion
  rawLoading.value = true
  error.value = ''
  try {
    const result = await api<DeliveryRawMessage>(queuePath('/' + props.job.id + '/raw'))
    if (version === requestVersion) raw.value = result
  }
  catch (cause) { if (version === requestVersion) error.value = cause instanceof Error ? cause.message : '原始邮件加载失败' }
  finally { if (version === requestVersion) rawLoading.value = false }
}

function queuePath(suffix = '') {
  const base = props.domainId ? '/domains/' + encodeURIComponent(props.domainId) + '/queue' : '/queues'
  return base + suffix
}

function close() { emit('update:modelValue', false) }

function downloadRaw() {
  if (!raw.value) return
  const url = globalThis.URL.createObjectURL(new globalThis.Blob([raw.value.content], { type: 'message/rfc822;charset=utf-8' }))
  const anchor = globalThis.document.createElement('a')
  anchor.href = url
  anchor.download = raw.value.fileName
  anchor.click()
  globalThis.setTimeout(() => globalThis.URL.revokeObjectURL(url), 0)
}

watch([() => props.modelValue, () => props.job?.id], ([value]) => {
  ++requestVersion
  rawLoading.value = false
  attemptsLoading.value = false
  if (!value || !props.job) return
  tab.value = 'attempts'
  attempts.value = []
  attemptsError.value = false
  raw.value = null
  error.value = ''
  void loadAttempts()
}, { immediate: true })

watch(tab, value => { if (value === 'message') void loadRaw() })
onBeforeUnmount(() => { ++requestVersion })
</script>

<template>
  <v-dialog :model-value="modelValue" max-width="1120" scrollable @update:model-value="emit('update:modelValue', $event)">
    <v-card v-if="job" class="delivery-detail-dialog">
      <header class="delivery-detail-head">
        <div>
          <div class="delivery-detail-title"><span>投递任务</span><code>{{ shortId(job.id) }}</code><StatusPill v-if="status" v-bind="status" /></div>
          <p>{{ job.sender }} <v-icon size="14">mdi-arrow-right</v-icon> {{ job.recipient }}</p>
        </div>
        <v-btn icon="mdi-close" variant="text" aria-label="关闭投递详情" @click="close" />
      </header>

      <v-tabs v-model="tab" color="primary" class="delivery-detail-tabs">
        <v-tab value="attempts">投递尝试 <span class="tab-count">{{ attempts.length }}</span></v-tab>
        <v-tab value="message">原始邮件</v-tab>
      </v-tabs>

      <v-alert v-if="error" type="error" variant="tonal" class="mx-6 mt-4" closable @click:close="error = ''">{{ error }}</v-alert>

      <v-window v-model="tab">
        <v-window-item value="attempts">
          <v-card-text class="delivery-attempts-content">
            <div class="delivery-facts">
              <div><span>创建时间</span><b>{{ formatDate(job.createdAt) }}</b></div>
              <div><span>最近更新</span><b>{{ formatDate(job.updatedAt) }}</b></div>
              <div><span>尝试次数</span><b>{{ job.attempts }}</b></div>
              <div><span>下次动作</span><b>{{ job.status === 'Pending' || job.status === 'Deferred' ? formatDate(job.nextAttemptAt) : '无需重试' }}</b></div>
            </div>

            <v-progress-linear v-if="attemptsLoading" indeterminate color="primary" class="mt-6" />
            <div v-else-if="attemptsError" class="delivery-empty">
              <v-icon size="30">mdi-alert-circle-outline</v-icon>
              <b>投递尝试加载失败</b>
              <v-btn variant="outlined" @click="loadAttempts">重新加载</v-btn>
            </div>
            <div v-else-if="!attempts.length" class="delivery-empty">
              <v-icon size="30">mdi-timeline-clock-outline</v-icon>
              <b>尚无投递尝试</b>
              <span>任务被 Worker 领取后，会在这里记录 DNS、连接、TLS 与 SMTP 阶段。</span>
            </div>
            <ol v-else class="attempt-timeline">
              <li v-for="attempt in attempts" :key="attempt.id" :class="{ 'is-success': attempt.succeeded, 'is-danger': attempt.permanentFailure }">
                <div class="attempt-marker"><v-icon size="16">{{ attempt.succeeded ? 'mdi-check' : attempt.permanentFailure ? 'mdi-close' : 'mdi-clock-outline' }}</v-icon></div>
                <article>
                  <header>
                    <div><b>第 {{ attempt.attemptNumber }} 次 · {{ stageLabels[attempt.stage] || attempt.stage }}</b><span>{{ formatDate(attempt.createdAt) }}</span></div>
                    <StatusPill :label="attempt.succeeded ? '成功' : attempt.permanentFailure ? '永久失败' : '临时失败'" :tone="attempt.succeeded ? 'success' : attempt.permanentFailure ? 'danger' : 'warning'" />
                  </header>
                  <div class="attempt-grid">
                    <div><span>MX 主机</span><b>{{ attempt.mxHost || '尚未解析' }}</b><small>{{ attempt.remoteAddress || '无远端地址' }}</small></div>
                    <div><span>SMTP 响应</span><b>{{ [attempt.smtpStatusCode, attempt.enhancedStatusCode].filter(Boolean).join(' · ') || '—' }}</b><small>{{ attempt.failureCategory || '无失败分类' }}</small></div>
                    <div><span>TLS</span><b>{{ attempt.tlsProtocol || '未建立 TLS' }}</b><small>{{ attempt.tlsCipherSuite || '—' }}</small></div>
                    <div><span>耗时</span><b>{{ attempt.durationMilliseconds.toLocaleString('zh-CN') }} ms</b><small>{{ attempt.nextRetryAt ? `下次 ${formatDate(attempt.nextRetryAt)}` : '无后续重试' }}</small></div>
                  </div>
                  <p v-if="attempt.response" class="attempt-response">{{ attempt.response }}</p>
                  <details v-if="attempt.sessionSummary" class="session-summary">
                    <summary>查看会话摘要 <span v-if="attempt.sessionSummary.includes('[敏感信息已脱敏]')">· 已脱敏</span><span v-if="attempt.sessionSummary.includes('[摘要已截断至 64 KiB]')">· 已截断</span></summary>
                    <pre>{{ attempt.sessionSummary }}</pre>
                  </details>
                </article>
              </li>
            </ol>
          </v-card-text>
        </v-window-item>

        <v-window-item value="message">
          <v-card-text class="raw-message-content">
            <div class="raw-message-toolbar">
              <div><b>原始 MIME 邮件</b><span>仅对具备队列读取权限的管理员开放；预览环境正文已脱敏。</span></div>
              <v-btn variant="outlined" prepend-icon="mdi-download-outline" :disabled="!raw" @click="downloadRaw">下载 .eml</v-btn>
            </div>
            <v-progress-linear v-if="rawLoading" indeterminate color="primary" />
            <pre v-else-if="raw" class="raw-message">{{ raw.content }}</pre>
            <div v-else class="delivery-empty"><span>原始邮件尚未加载。</span><v-btn variant="outlined" @click="loadRaw">重新加载</v-btn></div>
          </v-card-text>
        </v-window-item>
      </v-window>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.delivery-detail-dialog { border-radius: 12px !important; }
.delivery-detail-dialog > .v-window { overflow-y: auto; min-height: 0; }
.is-success .attempt-response { color: var(--color-success); }
.delivery-detail-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; padding: 22px 24px 16px; border-bottom: 1px solid var(--border); }
.delivery-detail-title { display: flex; align-items: center; gap: 10px; font-size: 18px; font-weight: 700; }
.delivery-detail-title code { font-size: 12px; font-weight: 600; color: var(--text-secondary); background: var(--bg-app); border-radius: 5px; padding: 3px 7px; }
.delivery-detail-head p { margin: 8px 0 0; color: var(--text-secondary); font-size: 13px; }
.delivery-detail-tabs { padding: 0 14px; border-bottom: 1px solid var(--border); }
.tab-count { margin-left: 6px; min-width: 20px; padding: 1px 6px; border-radius: 999px; background: var(--bg-app); color: var(--text-secondary); font-size: 11px; }
.delivery-attempts-content, .raw-message-content { padding: 22px 24px 28px !important; }
.delivery-facts { display: grid; grid-template-columns: repeat(4, 1fr); border: 1px solid var(--border); border-radius: 8px; background: #fbfcfe; }
.delivery-facts div { padding: 14px 16px; border-right: 1px solid var(--border); }
.delivery-facts div:last-child { border: 0; }
.delivery-facts span, .delivery-facts b { display: block; }
.delivery-facts span { color: var(--text-muted); font-size: 11px; margin-bottom: 5px; }
.delivery-facts b { font-size: 12px; }
.attempt-timeline { margin: 24px 0 0; padding: 0; list-style: none; }
.attempt-timeline li { position: relative; display: grid; grid-template-columns: 34px 1fr; gap: 12px; padding-bottom: 18px; }
.attempt-timeline li::before { content: ''; position: absolute; left: 16px; top: 30px; bottom: -4px; width: 1px; background: var(--border); }
.attempt-timeline li:last-child::before { display: none; }
.attempt-marker { position: relative; z-index: 1; width: 32px; height: 32px; display: grid; place-items: center; border-radius: 50%; background: #fff4dd; color: var(--color-warning); border: 1px solid #f1d59a; }
.is-success .attempt-marker { background: #e8f7f0; border-color: #bde6d3; color: var(--color-success); }
.is-danger .attempt-marker { background: #feecee; border-color: #f4c3c9; color: var(--color-danger); }
.attempt-timeline article { border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }
.attempt-timeline article > header { display: flex; align-items: center; justify-content: space-between; gap: 18px; padding: 13px 15px; background: #fafbfd; border-bottom: 1px solid var(--border); }
.attempt-timeline header b, .attempt-timeline header span { display: block; }
.attempt-timeline header b { font-size: 13px; }
.attempt-timeline header span { color: var(--text-muted); font-size: 11px; margin-top: 3px; }
.attempt-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); padding: 14px 15px; }
.attempt-grid div { min-width: 0; padding-right: 14px; }
.attempt-grid span, .attempt-grid b, .attempt-grid small { display: block; overflow-wrap: anywhere; }
.attempt-grid span { color: var(--text-muted); font-size: 10px; text-transform: uppercase; letter-spacing: .04em; }
.attempt-grid b { margin-top: 5px; font-size: 12px; }
.attempt-grid small { margin-top: 3px; color: var(--text-secondary); font-size: 11px; }
.attempt-response { margin: 0; padding: 0 15px 13px; color: var(--color-danger); font-size: 12px; }
.session-summary { border-top: 1px solid var(--border); }
.session-summary summary { padding: 11px 15px; cursor: pointer; color: var(--color-primary); font-size: 12px; }
.session-summary pre, .raw-message { margin: 0; padding: 16px; background: #111827; color: #d8e2f2; font: 12px/1.65 ui-monospace, SFMono-Regular, Consolas, monospace; white-space: pre-wrap; overflow-wrap: anywhere; }
.delivery-empty { min-height: 180px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; color: var(--text-muted); text-align: center; }
.delivery-empty b { color: var(--text); }
.raw-message-toolbar { display: flex; justify-content: space-between; align-items: center; gap: 20px; margin-bottom: 14px; }
.raw-message-toolbar b, .raw-message-toolbar span { display: block; }
.raw-message-toolbar span { margin-top: 4px; color: var(--text-muted); font-size: 12px; }
.raw-message { min-height: 420px; max-height: 58vh; overflow: auto; border-radius: 8px; }
@media (max-width: 900px) {
  .delivery-facts, .attempt-grid { grid-template-columns: 1fr 1fr; }
  .delivery-facts div:nth-child(2) { border-right: 0; }
  .delivery-facts div:nth-child(-n+2) { border-bottom: 1px solid var(--border); }
}
</style>
