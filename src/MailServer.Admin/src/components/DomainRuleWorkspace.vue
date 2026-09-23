<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import StatusPill from './StatusPill.vue'
import { api, jsonBody } from '../lib/api'
import {
  actionNeedsValues,
  bytesFromSizeInput,
  conditionError,
  fromLocalDateTimeInput,
  localTimezoneLabel,
  nextActionKind,
  normalizeActionsForSave,
  normalizeConditionsForSave,
  operatorWildcardHint,
  ruleActionError,
  ruleActionItems,
  ruleActionLabel,
  ruleActionPairError,
  ruleActionPlaceholder,
  ruleActionRowError,
  ruleConditionSummary,
  ruleOperators,
  ruleTogglePayload,
  sizeUnitOptions,
  splitMessageSize,
  toLocalDateTimeInput,
} from '../lib/mailRuleEditor'

interface RuleCondition {
  field: string
  operator: string
  values: string[]
  /** 邮件大小的编辑器草稿（仅界面使用，不提交）。 */
  sizeAmount?: string
  sizeUnit?: string
}

interface RuleAction {
  kind: string
  values: string[]
}

interface MailRule {
  id: string
  name: string
  direction: 'Incoming' | 'Outgoing'
  matchMode: 'All' | 'Any'
  targetMailboxName: string
  isEnabled: boolean
  priority: number
  conditions: RuleCondition[]
  actions: RuleAction[]
}

interface FolderCoverage {
  name: string
  availableAccounts: number
  totalAccounts: number
}

const props = defineProps<{
  domainId: string
  domainName: string
  canManage: boolean
}>()

type SectionKey = 'incoming' | 'outgoing'

const sections: Array<{ key: SectionKey; title: string; description: string; icon: string; direction: MailRule['direction'] }> = [
  { key: 'incoming', title: '收信规则', description: '邮件投递给本地账号前执行', icon: 'mdi-inbox-arrow-down-outline', direction: 'Incoming' },
  { key: 'outgoing', title: '发信规则', description: '已认证账号外发时执行', icon: 'mdi-send-outline', direction: 'Outgoing' },
]

const route = useRoute()
const router = useRouter()
const sectionKeys: SectionKey[] = ['incoming', 'outgoing']
function normalizeSection(value: unknown): SectionKey {
  return sectionKeys.includes(value as SectionKey) ? value as SectionKey : 'incoming'
}

/** 规则分区在 URL 中的位置：保留其它查询参数，清理旧版 tab/section query。 */
function rulesLocation(section: SectionKey) {
  const query = { ...route.query }
  delete query.tab
  delete query.section
  return { name: 'domain-detail' as const, params: { id: props.domainId, tab: 'rules', section }, query }
}

const activeSection = ref<SectionKey>(normalizeSection(route.params.section))
watch(() => route.params.section, value => {
  const next = normalizeSection(value)
  if (next !== activeSection.value) activeSection.value = next
})
watch(activeSection, value => {
  const clean = !('tab' in route.query) && !('section' in route.query)
  if (route.params.tab === 'rules' && String(route.params.section ?? '') === value && clean) return
  void router.replace(rulesLocation(value))
}, { immediate: true })

const direction = computed<MailRule['direction']>(() => sections.find(section => section.key === activeSection.value)!.direction)
const isIncoming = computed(() => direction.value === 'Incoming')
const currentSection = computed(() => sections.find(section => section.key === activeSection.value)!)

const rules = ref<MailRule[]>([])
const folders = ref<FolderCoverage[]>([])
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const notice = ref('')

const editorOpen = ref(false)
const editingId = ref<string | null>(null)
const editorError = ref('')
const editorNotice = ref('')
const draft = ref({ name: '', matchMode: 'All' as MailRule['matchMode'], isEnabled: true })
const conditions = ref<RuleCondition[]>([])
const actions = ref<RuleAction[]>([])

let loadToken = 0
let unmounted = false
// 当前列表实际对应的「域 + 方向」。切换后旧列表不允许再被操作。
const loadedKey = ref('')
const currentKey = computed(() => `${props.domainId}:${direction.value}`)
/** 列表已加载完成且确实对应当前域与方向，才允许对记录增删改排序。 */
const listReady = computed(() => !loading.value && loadedKey.value === currentKey.value)

/** 当前编辑器内容的稳定序列化，用于判断是否有未保存修改。 */
function editorSnapshot() {
  return JSON.stringify({
    name: draft.value.name,
    matchMode: draft.value.matchMode,
    isEnabled: draft.value.isEnabled,
    conditions: normalizeConditionsForSave(conditions.value.map(condition => ({ field: condition.field, operator: condition.operator, values: [...condition.values] }))),
    actions: normalizeActionsForSave(actions.value.map(action => ({ kind: action.kind, values: [...action.values] }))),
  })
}

let savedSnapshot = ''
const editorDirty = computed(() => editorOpen.value && editorSnapshot() !== savedSnapshot)

function protectPageExit(event: BeforeUnloadEvent) {
  if (!editorDirty.value && !saving.value) return
  event.preventDefault()
  event.returnValue = ''
}

watch(() => editorDirty.value || saving.value, needsProtection => {
  if (needsProtection) window.addEventListener('beforeunload', protectPageExit)
  else window.removeEventListener('beforeunload', protectPageExit)
})

function confirmDiscardDraft(message: string) {
  if (!editorDirty.value) return true
  return globalThis.confirm(message)
}

/** 记录必须来自当前已加载的列表，避免用旧方向/旧域的对象发起写请求。 */
function isRuleInCurrentList(rule: MailRule) {
  return loadedKey.value === currentKey.value && rules.value.some(item => item.id === rule.id)
}

async function load() {
  const token = ++loadToken
  const domainId = props.domainId
  const directionValue = direction.value
  const key = `${domainId}:${directionValue}`
  loading.value = true
  error.value = ''
  // 立即清空旧结果，避免切换分区/域后仍能对旧记录发起操作。
  rules.value = []
  folders.value = []
  loadedKey.value = ''
  try {
    const [nextRules, nextFolders] = await Promise.all([
      api<MailRule[]>(`/domains/${domainId}/rules?direction=${directionValue}`),
      api<FolderCoverage[]>(`/domains/${domainId}/rule-folders`),
    ])
    if (unmounted || token !== loadToken) return
    rules.value = nextRules
    folders.value = nextFolders
    loadedKey.value = key
  } catch (cause) {
    if (unmounted || token !== loadToken) return
    rules.value = []
    folders.value = []
    error.value = cause instanceof Error ? cause.message : '邮件规则加载失败'
  } finally {
    if (!unmounted && token === loadToken) loading.value = false
  }
}

watch([activeSection, () => props.domainId], () => {
  closeEditor()
  void load()
}, { immediate: true })

/** 切换收信/发信分区：先落 URL，未保存草稿在路由守卫里确认。 */
function requestSectionChange(key: SectionKey) {
  if (saving.value || key === activeSection.value) return
  void router.replace(rulesLocation(key))
}

onBeforeRouteUpdate((to, from) => {
  const paramsChanged = to.params.id !== from.params.id || to.params.tab !== from.params.tab || to.params.section !== from.params.section
  if (!paramsChanged) return true
  if (saving.value) return false
  return confirmDiscardDraft(`「${currentSection.value.title}」还有未保存的修改，离开后将丢失。确定离开吗？`)
})

onBeforeRouteLeave(() => {
  if (saving.value) return false
  return confirmDiscardDraft('离开当前页面将丢失未保存的规则修改。确定离开吗？')
})

onBeforeUnmount(() => {
  unmounted = true
  loadToken += 1
  window.removeEventListener('beforeunload', protectPageExit)
})

const folderItems = computed(() => folders.value.map(folder => folder.name))
const fieldItems = [
  { title: '发件人', value: 'Sender' },
  { title: '发件域', value: 'SenderDomain' },
  { title: '收件人', value: 'Recipient' },
  { title: '抄送', value: 'Cc' },
  { title: '收件人或抄送', value: 'RecipientOrCc' },
  { title: '主题', value: 'Subject' },
  { title: '正文', value: 'Body' },
  { title: '附件名', value: 'AttachmentName' },
  { title: '是否有附件', value: 'HasAttachment' },
  { title: '邮件时间', value: 'MessageTime' },
  { title: '邮件大小（字节）', value: 'MessageSize' },
]
const matchModeItems = [
  { title: '满足全部条件', value: 'All' },
  { title: '满足任一条件', value: 'Any' },
]
const actionItems = computed(() => ruleActionItems(direction.value))

const attachmentItems = [
  { title: '有附件', value: 'true' },
  { title: '无附件', value: 'false' },
]

function conditionValueLabel() {
  return '匹配值，支持 * 与 ? 通配符，回车添加'
}

function wildcardHint(condition: RuleCondition) {
  return operatorWildcardHint(condition.operator)
}

/** 邮件大小：数字与单位分开输入，提交前精确换算为字节。 */
function syncSize(condition: RuleCondition) {
  const bytes = bytesFromSizeInput(condition.sizeAmount ?? '', condition.sizeUnit ?? 'MiB')
  condition.values = bytes ? [bytes] : []
}

function setTimePart(condition: RuleCondition, index: number, localValue: unknown) {
  const between = condition.operator === 'Between'
  const next = between ? [condition.values[0] ?? '', condition.values[1] ?? ''] : [condition.values[0] ?? '']
  next[index] = fromLocalDateTimeInput(String(localValue ?? ''))
  condition.values = between ? [next[0] ?? '', next[1] ?? ''] : [next[0] ?? '']
}

function changeConditionOperator(condition: RuleCondition, operator: string) {
  condition.operator = operator
  if (condition.field === 'MessageTime') {
    condition.values = operator === 'Between' ? [condition.values[0] ?? '', condition.values[1] ?? ''] : [condition.values[0] ?? '']
  }
}

function attachmentValue(condition: RuleCondition) {
  return condition.values[0] ?? null
}

function setAttachment(condition: RuleCondition, value: unknown) {
  condition.values = value == null ? [] : [String(value)]
}

/** 新条件的默认字段：优先选当前未使用的字段，避免重复。 */
function nextConditionField() {
  const used = new Set(conditions.value.map(condition => condition.field))
  return fieldItems.find(item => !used.has(item.value))?.value ?? 'Subject'
}

const editorValid = computed(() => {
  if (!draft.value.name.trim() || draft.value.name.trim().length > 128) return false
  if (!conditions.value.length || conditions.value.length > 20) return false
  if (conditions.value.some(condition => conditionError(condition))) return false
  if (conditions.value.some(condition => condition.field === 'MessageSize' && !condition.values.length)) return false
  return !ruleActionError(direction.value, actions.value)
})

function openEditor(rule?: MailRule) {
  editingId.value = rule?.id ?? null
  draft.value = {
    name: rule?.name ?? '',
    matchMode: rule?.matchMode === 'Any' ? 'Any' : 'All',
    isEnabled: rule?.isEnabled ?? true,
  }
  conditions.value = (rule?.conditions ?? []).map(condition => {
    const next: RuleCondition = { field: condition.field, operator: condition.operator, values: [...condition.values] }
    if (next.field === 'MessageSize') {
      const draft = splitMessageSize(next.values[0] ?? '')
      next.sizeAmount = draft.amount
      next.sizeUnit = draft.unit
    }
    return next
  })
  if (!conditions.value.length) conditions.value = [newCondition()]
  actions.value = (rule?.actions ?? []).map(action => ({ kind: action.kind, values: [...action.values] }))
  editorError.value = ''
  editorNotice.value = ''
  // 旧规则可能没有执行动作：补一个默认动作并明确提示，避免静默改变规则行为。
  if (!actions.value.length && rule) {
    actions.value = [defaultAction(isIncoming.value)]
    editorNotice.value = '该规则没有执行动作（来自旧版本数据），已默认补充一个动作，请确认后再保存。'
  }
  if (!actions.value.length) actions.value = [defaultAction(isIncoming.value)]
  notice.value = ''
  savedSnapshot = ''
  editorOpen.value = true
  savedSnapshot = editorSnapshot()
}

function defaultAction(incoming: boolean): RuleAction {
  return incoming ? { kind: 'MoveToFolder', values: [] } : { kind: 'ForwardTo', values: [] }
}

/** 放弃草稿并关闭编辑器；调用方负责事先确认。 */
function closeEditor() {
  editorOpen.value = false
  editingId.value = null
  editorError.value = ''
  editorNotice.value = ''
  savedSnapshot = ''
}

function requestCloseEditor() {
  if (saving.value) return
  if (!confirmDiscardDraft('当前规则还有未保存的修改，返回列表后将丢失。确定返回吗？')) return
  closeEditor()
}

function newCondition(): RuleCondition {
  const field = nextConditionField()
  return { field, operator: ruleOperators(field)[0]!.value, values: [] }
}

function addCondition() {
  if (conditions.value.length >= 20) return
  conditions.value.push(newCondition())
}

function removeCondition(index: number) {
  if (conditions.value.length <= 1) return
  conditions.value.splice(index, 1)
}

function addAction() {
  if (actions.value.length >= 21) return
  const kind = nextActionKind(direction.value, actions.value)
  if (!kind) return
  actions.value.push({ kind, values: [] })
}

/** 是否还能添加一个不与现有动作冲突的动作。 */
const canAddAction = computed(() => actions.value.length < 21
  && nextActionKind(direction.value, actions.value) !== null)

function removeAction(index: number) {
  actions.value.splice(index, 1)
}

function changeConditionField(condition: RuleCondition, field: string) {
  condition.field = field
  condition.operator = ruleOperators(field)[0]!.value
  condition.values = []
  if (field === 'MessageSize') {
    condition.sizeAmount = ''
    condition.sizeUnit = 'MiB'
  } else {
    delete condition.sizeAmount
    delete condition.sizeUnit
  }
}

function changeActionKind(action: RuleAction, kind: string) {
  action.kind = kind
  action.values = []
}

const moveFolder = computed(() => actions.value.find(action => action.kind === 'MoveToFolder')?.values[0] ?? '')

async function save() {
  if (!editorValid.value || saving.value) return
  // 请求开始时固定目标域、规则 ID、方向与文案，避免保存期间切换页面后串写。
  const domainId = props.domainId
  const ruleId = editingId.value
  const sectionTitle = currentSection.value.title
  const payload = {
    name: draft.value.name.trim(),
    direction: direction.value,
    matchMode: draft.value.matchMode,
    targetMailboxName: moveFolder.value || (isIncoming.value ? 'INBOX' : 'Sent'),
    isEnabled: draft.value.isEnabled,
    conditions: conditions.value.map(condition => ({ field: condition.field, operator: condition.operator, values: [...condition.values] })),
    actions: actions.value.map(action => ({ kind: action.kind, values: [...action.values] })),
  }
  saving.value = true
  editorError.value = ''
  try {
    if (ruleId) await api(`/domains/${domainId}/rules/${ruleId}`, { method: 'PUT', body: jsonBody(payload) })
    else await api(`/domains/${domainId}/rules`, { method: 'POST', body: jsonBody(payload) })
    if (unmounted) return
    notice.value = ruleId ? `${sectionTitle}已保存。` : `${sectionTitle}已创建。`
    closeEditor()
    await load()
  } catch (cause) {
    if (unmounted) return
    editorError.value = cause instanceof Error ? cause.message : '规则保存失败'
  } finally {
    saving.value = false
  }
}

async function toggleRule(rule: MailRule) {
  if (saving.value || loading.value || !isRuleInCurrentList(rule)) return
  const domainId = props.domainId
  const ruleId = rule.id
  saving.value = true
  error.value = ''
  try {
    await api(`/domains/${domainId}/rules/${ruleId}`, { method: 'PUT', body: jsonBody(ruleTogglePayload(rule)) })
    const current = rules.value.find(item => item.id === ruleId)
    if (current) current.isEnabled = !current.isEnabled
    notice.value = `规则「${rule.name}」已${current?.isEnabled ? '启用' : '停用'}。`
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '规则状态更新失败'
  } finally {
    saving.value = false
  }
}

async function removeRule(rule: MailRule) {
  if (saving.value || loading.value || !isRuleInCurrentList(rule)) return
  if (!globalThis.confirm(`确认删除规则「${rule.name}」？该操作不可撤销。`)) return
  const domainId = props.domainId
  saving.value = true
  error.value = ''
  try {
    await api(`/domains/${domainId}/rules/${rule.id}`, { method: 'DELETE' })
    notice.value = `规则「${rule.name}」已删除。`
    if (editingId.value === rule.id) closeEditor()
    await load()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '规则删除失败'
  } finally {
    saving.value = false
  }
}

async function moveRule(rule: MailRule, offset: number) {
  if (saving.value || loading.value || !isRuleInCurrentList(rule)) return
  const domainId = props.domainId
  const ordered = [...rules.value]
  const index = ordered.findIndex(candidate => candidate.id === rule.id)
  const target = index + offset
  if (index < 0 || target < 0 || target >= ordered.length) return
  const [moved] = ordered.splice(index, 1)
  ordered.splice(target, 0, moved!)
  saving.value = true
  error.value = ''
  try {
    await api(`/domains/${domainId}/rules/order`, { method: 'PUT', body: jsonBody({ direction: ordered[0]!.direction, ruleIds: ordered.map(candidate => candidate.id) }) })
    if (unmounted) return
    await load()
    notice.value = '规则执行顺序已更新。'
  } catch (cause) {
    if (unmounted) return
    error.value = cause instanceof Error ? cause.message : '规则排序失败'
  } finally {
    saving.value = false
  }
}

function actionSummary(rule: MailRule) {
  if (!rule.actions.length) return rule.direction === 'Incoming' ? `投递到 ${rule.targetMailboxName}` : '保存 Sent 副本'
  return rule.actions.map(action => {
    const label = ruleActionLabel(action.kind, rule.direction)
    return action.values.length ? `${label}：${action.values.join('、')}` : label
  }).join(' · ')
}

function normalizeTagValues(value: unknown): string[] {
  if (!Array.isArray(value)) return []
  return value.map(item => String(item ?? '').trim()).filter(Boolean)
}

function actionValuePlaceholder(kind: string) {
  return ruleActionPlaceholder(kind)
}

function actionRowError(index: number) {
  return ruleActionRowError(direction.value, actions.value, index)
}

function actionPairError() {
  return ruleActionPairError(direction.value, actions.value)
}

function conditionRowError(condition: RuleCondition) {
  return conditionError(condition)
}
</script>

<template>
  <div class="rule-page">
    <nav class="rule-nav" aria-label="收发信规则分区">
      <button v-for="section in sections" :key="section.key" type="button" :class="{ active: activeSection === section.key }" :disabled="saving" @click="requestSectionChange(section.key)">
        <v-icon size="20">{{ section.icon }}</v-icon>
        <span><b>{{ section.title }}</b><small>{{ section.description }}</small></span>
        <v-icon size="16">mdi-chevron-right</v-icon>
      </button>
      <div class="nav-note">
        <v-icon size="17">mdi-information-outline</v-icon>
        <span>规则按优先级从上到下执行，命中第一条后停止继续匹配。</span>
      </div>
    </nav>

    <v-card flat class="panel rule-panel">
      <div class="section-header">
        <div>
          <span>收发信规则</span>
          <h2>{{ currentSection.title }}</h2>
          <p>{{ isIncoming ? '匹配条件支持 * 与 ? 通配符；可移动文件夹或转发给其他地址。' : '匹配条件支持 * 与 ? 通配符；可保存 Sent 副本、转发或直接拒绝发信。' }}</p>
        </div>
        <div class="section-actions">
          <StatusPill v-if="editorOpen && editorDirty" label="未保存" tone="warning" />
          <StatusPill v-else-if="editorOpen" label="编辑中" tone="neutral" />
          <v-btn v-else icon="mdi-refresh" variant="text" aria-label="刷新规则" :loading="loading" :disabled="saving" @click="load" />
          <v-btn v-if="canManage && !editorOpen" color="primary" prepend-icon="mdi-plus" :disabled="saving || loading" @click="openEditor()">新建{{ isIncoming ? '收信' : '发信' }}规则</v-btn>
        </div>
      </div>

      <v-alert v-if="error" type="error" variant="tonal" closable class="ma-5" @click:close="error = ''">{{ error }}</v-alert>
      <v-alert v-if="notice" type="success" variant="tonal" closable class="ma-5" @click:close="notice = ''">{{ notice }}</v-alert>

      <template v-if="!editorOpen">
        <div class="rule-list">
          <div v-for="(rule, index) in rules" :key="rule.id">
            <span class="rule-priority">{{ index + 1 }}</span>
            <span class="rule-main">
              <b>{{ rule.name }}</b>
              <small>{{ rule.matchMode === 'Any' ? '满足任一条件' : '满足全部条件' }} · {{ actionSummary(rule) }}</small>
              <span class="rule-conditions">
                <code v-for="(condition, position) in rule.conditions" :key="position">{{ ruleConditionSummary(condition) }}</code>
              </span>
            </span>
            <StatusPill :label="rule.isEnabled ? '已启用' : '已停用'" :tone="rule.isEnabled ? 'success' : 'neutral'" />
            <span v-if="canManage" class="rule-actions">
              <v-tooltip text="上移（更早执行）"><template #activator="{ props: tip }"><v-btn v-bind="tip" icon="mdi-arrow-up" size="small" variant="text" aria-label="上移规则" :disabled="index === 0 || saving || !listReady" @click="moveRule(rule, -1)" /></template></v-tooltip>
              <v-tooltip text="下移（更晚执行）"><template #activator="{ props: tip }"><v-btn v-bind="tip" icon="mdi-arrow-down" size="small" variant="text" aria-label="下移规则" :disabled="index === rules.length - 1 || saving || !listReady" @click="moveRule(rule, 1)" /></template></v-tooltip>
              <v-tooltip :text="rule.isEnabled ? '停用规则' : '启用规则'"><template #activator="{ props: tip }"><v-btn v-bind="tip" :icon="rule.isEnabled ? 'mdi-toggle-switch' : 'mdi-toggle-switch-off-outline'" size="small" variant="text" :aria-label="rule.isEnabled ? '停用规则' : '启用规则'" :disabled="saving || !listReady" @click="toggleRule(rule)" /></template></v-tooltip>
              <v-tooltip text="编辑规则"><template #activator="{ props: tip }"><v-btn v-bind="tip" icon="mdi-pencil-outline" size="small" variant="text" aria-label="编辑规则" :disabled="saving || !listReady" @click="openEditor(rule)" /></template></v-tooltip>
              <v-tooltip text="删除规则"><template #activator="{ props: tip }"><v-btn v-bind="tip" icon="mdi-delete-outline" size="small" variant="text" color="error" aria-label="删除规则" :disabled="saving || !listReady" @click="removeRule(rule)" /></template></v-tooltip>
            </span>
          </div>
          <div v-if="loading" class="rule-empty"><v-progress-linear indeterminate color="primary" /></div>
          <div v-else-if="!rules.length" class="rule-empty">尚未配置{{ currentSection.title }}，点击右上角新建。</div>
        </div>
        <div v-if="isIncoming" class="panel-action">
          <span>目标文件夹可用性</span>
          <span class="folder-coverage"><span v-for="folder in folders" :key="folder.name"><b>{{ folder.name }}</b>{{ folder.availableAccounts }}/{{ folder.totalAccounts }}</span></span>
        </div>
      </template>

      <v-form v-else class="rule-editor" :disabled="saving" :aria-busy="saving" @submit.prevent="save">
        <div class="editor-head">
          <v-btn variant="text" prepend-icon="mdi-arrow-left" :disabled="saving" @click="requestCloseEditor">返回规则列表</v-btn>
          <div><b>{{ editingId ? '编辑' : '新建' }}{{ currentSection.title }}</b><small>{{ isIncoming ? '邮件投递给本地账号前按顺序执行' : '已认证账号外发时按顺序执行' }}</small></div>
        </div>
        <v-alert v-if="editorError" type="error" variant="tonal" class="mb-4">{{ editorError }}</v-alert>
        <v-alert v-if="editorNotice" type="info" variant="tonal" class="mb-4" closable @click:close="editorNotice = ''">{{ editorNotice }}</v-alert>

        <div class="editor-grid">
          <v-text-field v-model="draft.name" label="规则名称" placeholder="例如：隔离高风险附件" maxlength="128" counter="128" />
          <v-select v-model="draft.matchMode" :items="matchModeItems" label="匹配方式" />
        </div>
        <div class="editor-switch">
          <v-switch v-model="draft.isEnabled" :label="editingId ? '启用该规则' : '创建后立即启用'" hide-details color="primary" />
          <small>停用时规则保留配置但不参与匹配。</small>
        </div>

        <div class="editor-section">
          <div class="editor-section-head">
            <div><b>匹配条件</b><small>文本值支持 * 与 ? 通配符，输入后按回车生成标签</small></div>
            <v-btn size="small" variant="text" prepend-icon="mdi-plus" :disabled="conditions.length >= 20 || saving" @click="addCondition">添加条件</v-btn>
          </div>
          <div v-for="(condition, index) in conditions" :key="`condition-${index}`" class="condition-block">
            <div class="editor-row editor-row--condition">
              <v-select :model-value="condition.field" :items="fieldItems" label="字段" hide-details @update:model-value="value => changeConditionField(condition, value as string)" />
              <v-select :model-value="condition.operator" :items="ruleOperators(condition.field)" label="运算符" hide-details @update:model-value="value => changeConditionOperator(condition, value as string)" />
              <div class="condition-value">
                <v-select
                  v-if="condition.field === 'HasAttachment'"
                  :model-value="attachmentValue(condition)"
                  :items="attachmentItems"
                  label="附件情况"
                  hide-details
                  @update:model-value="value => setAttachment(condition, value)"
                />
                <div v-else-if="condition.field === 'MessageSize'" class="size-input">
                  <v-text-field
                    v-model="condition.sizeAmount"
                    type="number"
                    min="0"
                    label="邮件大小"
                    hide-details
                    @update:model-value="syncSize(condition)"
                  />
                  <v-select
                    v-model="condition.sizeUnit"
                    :items="sizeUnitOptions"
                    label="单位"
                    hide-details
                    @update:model-value="syncSize(condition)"
                  />
                </div>
                <div v-else-if="condition.field === 'MessageTime'" class="time-input">
                  <v-text-field
                    :model-value="toLocalDateTimeInput(condition.values[0] ?? '')"
                    type="datetime-local"
                    :label="condition.operator === 'Between' ? '开始时间' : '时间'"
                    hide-details
                    @update:model-value="value => setTimePart(condition, 0, value)"
                  />
                  <v-text-field
                    v-if="condition.operator === 'Between'"
                    :model-value="toLocalDateTimeInput(condition.values[1] ?? '')"
                    type="datetime-local"
                    label="结束时间"
                    hide-details
                    @update:model-value="value => setTimePart(condition, 1, value)"
                  />
                  <small class="size-note">按浏览器本地时区 {{ localTimezoneLabel() }}，保存时写入明确偏移。</small>
                </div>
                <v-combobox
                  v-else
                  v-model="condition.values"
                  multiple
                  chips
                  closable-chips
                  clearable
                  hide-details
                  :label="conditionValueLabel()"
                  @update:model-value="value => condition.values = normalizeTagValues(value)"
                />
              </div>
              <v-btn icon="mdi-close" size="small" variant="text" aria-label="移除条件" :disabled="conditions.length <= 1 || saving" @click="removeCondition(index)" />
            </div>
            <p v-if="wildcardHint(condition)" class="editor-hint">{{ wildcardHint(condition) }}</p>
            <p v-if="conditionRowError(condition)" class="editor-hint is-error">条件 {{ index + 1 }}：{{ conditionRowError(condition) }}</p>
          </div>
        </div>

        <div class="editor-section">
          <div class="editor-section-head">
            <div><b>执行动作</b><small>{{ isIncoming ? '命中后按列出的动作执行，可同时移动与转发。' : '命中后可保存 Sent 副本到指定文件夹、转发副本，或直接拒绝发信（SMTP 阶段退信）。' }}</small></div>
            <v-btn size="small" variant="text" prepend-icon="mdi-plus" :disabled="!canAddAction || saving" @click="addAction">添加动作</v-btn>
          </div>
          <div v-for="(action, index) in actions" :key="`action-${index}`" class="action-block">
          <div class="editor-row editor-row--action">
            <v-select :model-value="action.kind" :items="actionItems" label="动作" hide-details @update:model-value="value => changeActionKind(action, value as string)" />
            <v-select v-if="action.kind === 'MoveToFolder'" :model-value="action.values[0] ?? null" :items="folderItems" :label="isIncoming ? '目标文件夹' : 'Sent 副本文件夹'" hide-details @update:model-value="value => action.values = value ? [String(value)] : []" />
            <v-combobox
              v-else-if="actionNeedsValues(action.kind)"
              v-model="action.values"
              multiple
              chips
              closable-chips
              clearable
              hide-details
              :label="actionValuePlaceholder(action.kind)"
              @update:model-value="value => action.values = normalizeTagValues(value)"
            />
            <v-text-field v-else v-model="action.values[0]" label="拒绝理由（可留空）" hide-details maxlength="255" />
            <v-btn icon="mdi-close" size="small" variant="text" aria-label="移除动作" :disabled="saving" @click="removeAction(index)" />
          </div>
          <p v-if="actionRowError(index)" class="editor-hint is-error">动作 {{ index + 1 }}：{{ actionRowError(index) }}</p>
          </div>
          <p v-if="actionPairError()" class="editor-hint is-error">{{ actionPairError() }}</p>
        </div>

        <div class="editor-actions">
          <v-spacer />
          <v-btn variant="text" :disabled="saving" @click="requestCloseEditor">取消</v-btn>
          <v-btn color="primary" :disabled="!editorValid || !editorDirty" :loading="saving" @click="save">{{ editingId ? '保存规则' : '创建规则' }}</v-btn>
        </div>
      </v-form>
    </v-card>
  </div>
</template>

<style scoped>
.rule-page { display: grid; grid-template-columns: 268px minmax(0, 1fr); gap: 18px; align-items: start; }
.rule-nav { position: sticky; top: 76px; overflow: hidden; border: 1px solid var(--border); border-radius: 10px; background: #fff; }
.rule-nav button { display: grid; grid-template-columns: 25px minmax(0, 1fr) 18px; align-items: center; gap: 10px; width: 100%; padding: 14px 15px; border: 0; border-bottom: 1px solid var(--border); color: var(--text-secondary); background: transparent; text-align: left; cursor: pointer; }
.rule-nav button:hover { background: #f8fafc; }
.rule-nav button.active { color: #175cb8; background: #f2f7ff; box-shadow: inset 3px 0 #2c72d6; }
.rule-nav button span { display: flex; flex-direction: column; gap: 2px; }
.rule-nav button b { font-size: 13px; }
.rule-nav button small { color: var(--text-muted); font-size: 11px; }
.nav-note { display: flex; gap: 8px; padding: 12px 14px; color: var(--text-muted); background: #fafbfc; font-size: 11px; line-height: 1.6; }
.nav-note :deep(.v-icon) { flex: none; margin-top: 2px; }
.section-header { display: flex; align-items: center; justify-content: space-between; gap: 14px; flex-wrap: wrap; padding: 18px 22px; border-bottom: 1px solid var(--border); }
.section-header span { color: var(--text-muted); font-size: 11px; letter-spacing: .08em; text-transform: uppercase; }
.section-header h2 { margin: 3px 0 0; color: var(--text); font-size: 17px; }
.section-header p { margin: 4px 0 0; color: var(--text-muted); font-size: 12px; }
.section-actions { display: flex; align-items: center; gap: 8px; }
.rule-list > div { display: grid; grid-template-columns: 40px minmax(0, 1fr) auto auto; gap: 12px; align-items: center; padding: 12px 20px; border-bottom: 1px solid var(--border); }
.rule-list > div:last-of-type { border-bottom: 0; }
.rule-priority { display: grid; width: 28px; height: 28px; place-items: center; color: #436385; background: #edf3f9; border-radius: 6px; font-size: 11px; font-weight: 700; }
.rule-main { display: flex; min-width: 0; flex-direction: column; gap: 3px; }
.rule-main b { color: var(--text); font-size: 13px; }
.rule-main small { color: var(--text-muted); font-size: 11px; }
.rule-conditions { display: flex; flex-wrap: wrap; gap: 6px; }
.rule-conditions code { padding: 2px 7px; color: #3d5170; background: #f1f5fa; border-radius: 5px; font-size: 10.5px; }
.rule-actions { display: flex; align-items: center; gap: 2px; }
.rule-empty { padding: 30px 20px; color: var(--text-muted); font-size: 12px; text-align: center; }
.panel-action { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 12px 20px; border-top: 1px solid var(--border); color: var(--text-muted); font-size: 11px; }
.folder-coverage { display: flex; flex-wrap: wrap; gap: 12px; }
.folder-coverage b { margin-right: 4px; color: var(--text); }
.rule-editor { padding: 20px 22px 24px; }
.editor-head { display: flex; align-items: center; gap: 14px; margin-bottom: 16px; }
.editor-head div { display: flex; flex-direction: column; }
.editor-head b { color: var(--text); font-size: 15px; }
.editor-head small { color: var(--text-muted); font-size: 11px; }
.editor-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 4px 16px; }
.editor-switch { display: flex; align-items: center; gap: 12px; margin: 4px 0 8px; }
.editor-switch small { color: var(--text-muted); font-size: 11px; }
.editor-section { margin-top: 16px; padding-top: 16px; border-top: 1px solid var(--border); }
.editor-section-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 10px; }
.editor-section-head b { display: block; font-size: 13px; }
.editor-section-head small { color: var(--text-muted); font-size: 11px; }
.condition-block, .action-block { padding: 10px 0 4px; border-bottom: 1px dashed var(--border); }
.condition-block:last-of-type, .action-block:last-of-type { border-bottom: 0; }
.editor-row { display: grid; grid-template-columns: 150px 150px minmax(0, 1fr) 40px; gap: 8px; align-items: start; margin-bottom: 8px; }
.editor-row--action { grid-template-columns: 170px minmax(0, 1fr) 40px; }
.condition-value { min-width: 0; }
.size-input { display: grid; grid-template-columns: minmax(0, 1fr) 160px; gap: 8px; }
.time-input { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
.size-note { grid-column: 1 / -1; color: var(--text-muted); font-size: 11px; }
.editor-hint { margin: 8px 0 0; color: var(--text-muted); font-size: 11px; }
.editor-hint.is-error { color: var(--error, #c43131); }
.editor-actions { display: flex; align-items: center; gap: 8px; margin-top: 20px; padding-top: 16px; border-top: 1px solid var(--border); }
@media (max-width: 1100px) {
  .rule-page { grid-template-columns: 1fr; }
  .rule-nav { position: static; display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .rule-nav button { border-right: 1px solid var(--border); }
  .nav-note { grid-column: 1 / -1; }
}
@media (max-width: 720px) {
  .editor-grid, .editor-row, .editor-row--action, .size-input, .time-input { grid-template-columns: 1fr; }
  .rule-nav { grid-template-columns: 1fr; }
}
</style>
