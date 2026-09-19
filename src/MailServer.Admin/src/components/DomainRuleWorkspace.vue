<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import StatusPill from './StatusPill.vue'
import { api, jsonBody } from '../lib/api'
import {
  actionNeedsValues,
  conditionError,
  ruleActionError,
  ruleActionItems,
  ruleActionLabel,
  ruleActionPlaceholder,
  ruleConditionSummary,
  ruleOperators,
  ruleTogglePayload,
} from '../lib/mailRuleEditor'

interface RuleCondition {
  field: string
  operator: string
  values: string[]
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
  if (route.params.tab === 'rules' && String(route.params.section ?? '') === value && clean) return
  void router.replace({ name: 'domain-detail', params: { id: props.domainId, tab: 'rules', section: value }, query })
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
const draft = ref({ name: '', matchMode: 'All' as MailRule['matchMode'], isEnabled: true })
const conditions = ref<RuleCondition[]>([])
const actions = ref<RuleAction[]>([])

let loadToken = 0

async function load() {
  const token = ++loadToken
  loading.value = true
  error.value = ''
  try {
    const [nextRules, nextFolders] = await Promise.all([
      api<MailRule[]>(`/domains/${props.domainId}/rules?direction=${direction.value}`),
      api<FolderCoverage[]>(`/domains/${props.domainId}/rule-folders`),
    ])
    if (token !== loadToken) return
    rules.value = nextRules
    folders.value = nextFolders
  } catch (cause) {
    if (token !== loadToken) return
    rules.value = []
    error.value = cause instanceof Error ? cause.message : '邮件规则加载失败'
  } finally {
    if (token === loadToken) loading.value = false
  }
}

watch([activeSection, () => props.domainId], () => {
  closeEditor()
  void load()
}, { immediate: true })

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

function conditionValueLabel(condition: RuleCondition) {
  if (condition.field === 'MessageSize') return '字节数（1 MiB = 1048576）'
  if (condition.field === 'HasAttachment') return 'true（有）或 false（无）'
  if (condition.field === 'MessageTime') return '带时区时间，例如 2026-09-19T08:00:00+08:00'
  return '匹配值，支持 * 与 ? 通配符，回车添加'
}

function conditionItems(condition: RuleCondition) {
  if (condition.field === 'HasAttachment') return ['true', 'false']
  if (condition.field === 'MessageSize') return []
  if (condition.field === 'MessageTime') return []
  if (condition.field === 'Sender') return []
  return []
}

const editorValid = computed(() => {
  if (!draft.value.name.trim() || draft.value.name.trim().length > 128) return false
  if (!conditions.value.length || conditions.value.length > 20) return false
  if (conditions.value.some(condition => conditionError(condition))) return false
  return !ruleActionError(direction.value, actions.value)
})

function openEditor(rule?: MailRule) {
  editingId.value = rule?.id ?? null
  draft.value = {
    name: rule?.name ?? '',
    matchMode: rule?.matchMode === 'Any' ? 'Any' : 'All',
    isEnabled: rule?.isEnabled ?? true,
  }
  conditions.value = (rule?.conditions ?? []).map(condition => ({
    field: condition.field,
    operator: condition.operator,
    values: [...condition.values],
  }))
  if (!conditions.value.length) conditions.value = [{ field: isIncoming.value ? 'Sender' : 'Recipient', operator: 'EqualsAny', values: [] }]
  actions.value = (rule?.actions ?? []).map(action => ({ kind: action.kind, values: [...action.values] }))
  if (!actions.value.length) actions.value = [defaultAction(isIncoming.value)]
  editorError.value = ''
  notice.value = ''
  editorOpen.value = true
}

function defaultAction(incoming: boolean): RuleAction {
  return incoming ? { kind: 'MoveToFolder', values: [] } : { kind: 'ForwardTo', values: [] }
}

function closeEditor() {
  editorOpen.value = false
  editingId.value = null
  editorError.value = ''
}

function addCondition() {
  conditions.value.push({ field: 'Subject', operator: 'ContainsAny', values: [] })
}

function removeCondition(index: number) {
  if (conditions.value.length <= 1) return
  conditions.value.splice(index, 1)
}

function addAction() {
  actions.value.push(defaultAction(isIncoming.value))
}

function removeAction(index: number) {
  actions.value.splice(index, 1)
}

function changeConditionField(condition: RuleCondition, field: string) {
  condition.field = field
  condition.operator = ruleOperators(field)[0]!.value
  condition.values = []
}

function changeActionKind(action: RuleAction, kind: string) {
  action.kind = kind
  action.values = []
}

const moveFolder = computed(() => actions.value.find(action => action.kind === 'MoveToFolder')?.values[0] ?? '')

async function save() {
  if (!editorValid.value || saving.value) return
  const payload = {
    name: draft.value.name.trim(),
    direction: direction.value,
    matchMode: draft.value.matchMode,
    targetMailboxName: isIncoming.value ? (moveFolder.value || 'INBOX') : 'Sent',
    isEnabled: draft.value.isEnabled,
    conditions: conditions.value.map(condition => ({ field: condition.field, operator: condition.operator, values: condition.values })),
    actions: actions.value.map(action => ({ kind: action.kind, values: action.values })),
  }
  saving.value = true
  editorError.value = ''
  try {
    if (editingId.value) await api(`/domains/${props.domainId}/rules/${editingId.value}`, { method: 'PUT', body: jsonBody(payload) })
    else await api(`/domains/${props.domainId}/rules`, { method: 'POST', body: jsonBody(payload) })
    notice.value = editingId.value ? `${currentSection.value.title}已保存。` : `${currentSection.value.title}已创建。`
    closeEditor()
    await load()
  } catch (cause) {
    editorError.value = cause instanceof Error ? cause.message : '规则保存失败'
  } finally {
    saving.value = false
  }
}

async function toggleRule(rule: MailRule) {
  if (saving.value) return
  saving.value = true
  error.value = ''
  try {
    await api(`/domains/${props.domainId}/rules/${rule.id}`, { method: 'PUT', body: jsonBody(ruleTogglePayload(rule)) })
    rule.isEnabled = !rule.isEnabled
    notice.value = `规则「${rule.name}」已${rule.isEnabled ? '启用' : '停用'}。`
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '规则状态更新失败'
  } finally {
    saving.value = false
  }
}

async function removeRule(rule: MailRule) {
  if (saving.value) return
  if (!globalThis.confirm(`确认删除规则「${rule.name}」？该操作不可撤销。`)) return
  saving.value = true
  error.value = ''
  try {
    await api(`/domains/${props.domainId}/rules/${rule.id}`, { method: 'DELETE' })
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
  const ordered = [...rules.value]
  const index = ordered.findIndex(candidate => candidate.id === rule.id)
  const target = index + offset
  if (index < 0 || target < 0 || target >= ordered.length || saving.value) return
  const [moved] = ordered.splice(index, 1)
  ordered.splice(target, 0, moved!)
  saving.value = true
  error.value = ''
  try {
    await api(`/domains/${props.domainId}/rules/order`, { method: 'PUT', body: jsonBody({ direction: direction.value, ruleIds: ordered.map(candidate => candidate.id) }) })
    rules.value = ordered.map((candidate, position) => ({ ...candidate, priority: (position + 1) * 10 }))
    notice.value = '规则执行顺序已更新。'
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '规则排序失败'
  } finally {
    saving.value = false
  }
}

function actionSummary(rule: MailRule) {
  if (!rule.actions.length) return rule.direction === 'Incoming' ? `投递到 ${rule.targetMailboxName}` : '保存 Sent 副本'
  return rule.actions.map(action => {
    const label = ruleActionLabel(action.kind)
    return action.values.length ? `${label}：${action.values.join('、')}` : label
  }).join(' · ')
}

function actionValuePlaceholder(kind: string) {
  return ruleActionPlaceholder(kind)
}
</script>

<template>
  <div class="rule-page">
    <nav class="rule-nav" aria-label="收发信规则分区">
      <button v-for="section in sections" :key="section.key" :class="{ active: activeSection === section.key }" @click="activeSection = section.key">
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
          <p>{{ isIncoming ? '匹配条件支持 * 与 ? 通配符；可移动文件夹或转发给其他地址。' : '匹配条件支持 * 与 ? 通配符；可转发或直接拒绝发信。' }}</p>
        </div>
        <div class="section-actions">
          <StatusPill v-if="editorOpen" label="编辑中" tone="warning" />
          <v-btn v-else icon="mdi-refresh" variant="text" aria-label="刷新规则" :loading="loading" @click="load" />
          <v-btn v-if="canManage && !editorOpen" color="primary" prepend-icon="mdi-plus" @click="openEditor()">新建{{ isIncoming ? '收信' : '发信' }}规则</v-btn>
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
              <v-tooltip text="上移（更早执行）"><template #activator="{ props: tip }"><v-btn v-bind="tip" icon="mdi-arrow-up" size="small" variant="text" aria-label="上移规则" :disabled="index === 0 || saving" @click="moveRule(rule, -1)" /></template></v-tooltip>
              <v-tooltip text="下移（更晚执行）"><template #activator="{ props: tip }"><v-btn v-bind="tip" icon="mdi-arrow-down" size="small" variant="text" aria-label="下移规则" :disabled="index === rules.length - 1 || saving" @click="moveRule(rule, 1)" /></template></v-tooltip>
              <v-tooltip :text="rule.isEnabled ? '停用规则' : '启用规则'"><template #activator="{ props: tip }"><v-btn v-bind="tip" :icon="rule.isEnabled ? 'mdi-toggle-switch' : 'mdi-toggle-switch-off-outline'" size="small" variant="text" :aria-label="rule.isEnabled ? '停用规则' : '启用规则'" :disabled="saving" @click="toggleRule(rule)" /></template></v-tooltip>
              <v-tooltip text="编辑规则"><template #activator="{ props: tip }"><v-btn v-bind="tip" icon="mdi-pencil-outline" size="small" variant="text" aria-label="编辑规则" :disabled="saving" @click="openEditor(rule)" /></template></v-tooltip>
              <v-tooltip text="删除规则"><template #activator="{ props: tip }"><v-btn v-bind="tip" icon="mdi-delete-outline" size="small" variant="text" color="error" aria-label="删除规则" :disabled="saving" @click="removeRule(rule)" /></template></v-tooltip>
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

      <div v-else class="rule-editor">
        <div class="editor-head">
          <v-btn variant="text" prepend-icon="mdi-arrow-left" @click="closeEditor">返回规则列表</v-btn>
          <div><b>{{ editingId ? '编辑' : '新建' }}{{ currentSection.title }}</b><small>{{ isIncoming ? '邮件投递给本地账号前按顺序执行' : '已认证账号外发时按顺序执行' }}</small></div>
        </div>
        <v-alert v-if="editorError" type="error" variant="tonal" class="mb-4">{{ editorError }}</v-alert>

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
            <div><b>匹配条件</b><small>值支持 * 与 ? 通配符，输入后按回车生成标签</small></div>
            <v-btn size="small" variant="text" prepend-icon="mdi-plus" :disabled="conditions.length >= 20" @click="addCondition">添加条件</v-btn>
          </div>
          <div v-for="(condition, index) in conditions" :key="`condition-${index}`" class="editor-row">
            <v-select :model-value="condition.field" :items="fieldItems" label="字段" hide-details @update:model-value="value => changeConditionField(condition, value as string)" />
            <v-select v-model="condition.operator" :items="ruleOperators(condition.field)" label="运算符" hide-details />
            <v-combobox
              v-model="condition.values"
              :items="conditionItems(condition)"
              multiple
              chips
              closable-chips
              clearable
              hide-details
              :label="conditionValueLabel(condition)"
              @update:model-value="value => condition.values = (value as string[]).map(item => String(item ?? '').trim()).filter(Boolean)"
            />
            <v-btn icon="mdi-close" size="small" variant="text" aria-label="移除条件" :disabled="conditions.length <= 1" @click="removeCondition(index)" />
          </div>
          <p v-if="conditions.some(condition => conditionError(condition))" class="editor-hint is-error">
            {{ conditions.map(condition => conditionError(condition)).filter(Boolean)[0] }}
          </p>
        </div>

        <div class="editor-section">
          <div class="editor-section-head">
            <div><b>执行动作</b><small>{{ isIncoming ? '命中后按列出的动作执行，可同时移动与转发。' : '命中后转发副本；选择拒绝发信会在 SMTP 阶段直接退信。' }}</small></div>
            <v-btn size="small" variant="text" prepend-icon="mdi-plus" :disabled="actions.length >= 21" @click="addAction">添加动作</v-btn>
          </div>
          <div v-for="(action, index) in actions" :key="`action-${index}`" class="editor-row editor-row--action">
            <v-select :model-value="action.kind" :items="actionItems" label="动作" hide-details @update:model-value="value => changeActionKind(action, value as string)" />
            <v-select v-if="action.kind === 'MoveToFolder'" :model-value="action.values[0] ?? null" :items="folderItems" label="目标文件夹" hide-details @update:model-value="value => action.values = value ? [String(value)] : []" />
            <v-combobox
              v-else-if="actionNeedsValues(action.kind)"
              v-model="action.values"
              multiple
              chips
              closable-chips
              clearable
              hide-details
              :label="actionValuePlaceholder(action.kind)"
              @update:model-value="value => action.values = (value as string[]).map(item => String(item ?? '').trim()).filter(Boolean)"
            />
            <v-text-field v-else v-model="action.values[0]" label="拒绝理由（可留空）" hide-details maxlength="255" />
            <v-btn icon="mdi-close" size="small" variant="text" aria-label="移除动作" @click="removeAction(index)" />
          </div>
          <p v-if="ruleActionError(direction, actions)" class="editor-hint is-error">{{ ruleActionError(direction, actions) }}</p>
        </div>

        <div class="editor-actions">
          <v-spacer />
          <v-btn variant="text" :disabled="saving" @click="closeEditor">取消</v-btn>
          <v-btn color="primary" :disabled="!editorValid" :loading="saving" @click="save">{{ editingId ? '保存规则' : '创建规则' }}</v-btn>
        </div>
      </div>
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
.editor-row { display: grid; grid-template-columns: 150px 150px minmax(0, 1fr) 40px; gap: 8px; align-items: center; margin-bottom: 8px; }
.editor-row--action { grid-template-columns: 170px minmax(0, 1fr) 40px; }
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
  .editor-grid, .editor-row, .editor-row--action { grid-template-columns: 1fr; }
  .rule-nav { grid-template-columns: 1fr; }
}
</style>
