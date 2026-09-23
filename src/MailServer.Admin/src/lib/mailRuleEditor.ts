export function ruleOperators(field: string) {
  if (field === 'MessageTime') return [
    { title: '早于', value: 'Before' }, { title: '晚于', value: 'After' }, { title: '介于', value: 'Between' },
  ]
  if (field === 'HasAttachment') return [{ title: '是否包含附件', value: 'Is' }]
  if (field === 'MessageSize') return [
    { title: '大于', value: 'GreaterThan' },
    { title: '大于或等于', value: 'GreaterThanOrEqual' },
    { title: '小于', value: 'LessThan' },
    { title: '小于或等于', value: 'LessThanOrEqual' },
  ]
  return [
    { title: '等于任意一项', value: 'EqualsAny' },
    { title: '包含任意一项', value: 'ContainsAny' },
    { title: '包含全部', value: 'ContainsAll' },
    { title: '不包含任意一项', value: 'NotContainsAny' },
  ]
}

/** 各字段在界面与规则摘要中的中文名。 */
export const conditionFieldLabels: Record<string, string> = {
  Sender: '发件人',
  SenderDomain: '发件域',
  Recipient: '收件人',
  Cc: '抄送',
  RecipientOrCc: '收件人或抄送',
  Subject: '主题',
  Body: '正文',
  AttachmentName: '附件名',
  HasAttachment: '是否有附件',
  MessageTime: '邮件时间',
  MessageSize: '邮件大小',
}

export function conditionFieldLabel(field: string) {
  return conditionFieldLabels[field] ?? field
}

/** 通配符在不同运算符下的实际语义，用于界面提示。 */
export function operatorWildcardHint(operator: string) {
  if (operator === 'EqualsAny') return '不含通配符时要求整串完全相同；* 匹配任意多个字符，? 匹配单个字符，含通配符时按整串匹配。'
  if (operator === 'ContainsAny') return '不含通配符时按子串包含判断；含 * 或 ? 时改为整串通配匹配。'
  if (operator === 'ContainsAll') return '每个值都要命中至少一个字段值；含 * 或 ? 时按整串通配匹配。'
  if (operator === 'NotContainsAny') return '所有值都不包含时才算命中；含 * 或 ? 时按整串通配匹配。'
  return ''
}

export interface ConditionLike { field: string; operator: string; values: string[] }

export function conditionError(condition: ConditionLike) {
  if (!ruleOperators(condition.field).some(item => item.value === condition.operator)) return '请选择该字段支持的运算符'
  const values = condition.values.map(value => value.trim()).filter(Boolean)
  if (!values.length) {
    if (condition.field === 'MessageSize') return '请填写邮件大小'
    if (condition.field === 'HasAttachment') return '请选择是否有附件'
    if (condition.field === 'MessageTime') return condition.operator === 'Between' ? '请选择开始与结束时间' : '请选择时间'
    return '请填写 1–50 个匹配值'
  }
  if (values.length > 50) return '匹配值最多 50 个'
  if (condition.field === 'MessageSize' && (values.length !== 1 || !/^\d+$/.test(values[0]!) || BigInt(values[0]!) > 9223372036854775807n)) return '邮件大小请输入一个非负数字'
  if (condition.field === 'HasAttachment' && (values.length !== 1 || !/^(true|false)$/i.test(values[0]!))) return '请选择“有附件”或“无附件”'
  if (condition.field === 'MessageTime') {
    const count = condition.operator === 'Between' ? 2 : 1
    if (values.length !== count || values.some(value => !/(Z|[+-]\d{2}:\d{2})$/i.test(value) || !Number.isFinite(Date.parse(value)))) return count === 2 ? '请选择开始与结束时间' : '请选择时间'
    if (count === 2 && Date.parse(values[0]!) > Date.parse(values[1]!)) return '开始时间不能晚于结束时间'
  }
  return ''
}

/** 邮件大小的单位选项；换算基数与后端字节字段一致。 */
export const sizeUnitOptions = [
  { title: '字节（B）', value: 'B', bytes: 1 },
  { title: 'KiB（1024 B）', value: 'KiB', bytes: 1024 },
  { title: 'MiB（1048576 B）', value: 'MiB', bytes: 1048576 },
  { title: 'GiB（1073741824 B）', value: 'GiB', bytes: 1073741824 },
] as const

/** 把「数字 + 单位」精确换算成字节字符串；非法输入返回空串。 */
export function bytesFromSizeInput(amount: string, unit: string): string {
  const match = /^(\d+)(?:[.．](\d+))?$/.exec(amount.trim())
  if (!match) return ''
  const option = sizeUnitOptions.find(item => item.value === unit)
  if (!option) return ''
  const whole = BigInt(match[1]!)
  const fraction = match[2] ?? ''
  const scale = 10n ** BigInt(fraction.length)
  const numerator = whole * scale + BigInt(fraction || '0')
  const bytes = numerator * BigInt(option.bytes) / scale
  if (bytes > 9223372036854775807n) return ''
  return bytes.toString()
}

/** 把字节数拆成最合适的单位，供编辑器回显。 */
export function splitMessageSize(bytes: string): { amount: string; unit: string } {
  const trimmed = bytes.trim()
  if (!/^\d+$/.test(trimmed)) return { amount: '', unit: 'MiB' }
  const total = BigInt(trimmed)
  for (const option of [...sizeUnitOptions].reverse()) {
    const factor = BigInt(option.bytes)
    if (total >= factor && total % factor === 0n) return { amount: (total / factor).toString(), unit: option.value }
  }
  return { amount: total.toString(), unit: 'B' }
}

/** 规则摘要里展示大小：优先用可读单位，同时保留精确字节数。 */
export function formatMessageSize(bytes: string): string {
  const trimmed = bytes.trim()
  if (!/^\d+$/.test(trimmed)) return bytes
  const { amount, unit } = splitMessageSize(trimmed)
  return unit === 'B' ? amount + ' 字节' : amount + ' ' + unit + '（' + trimmed + ' 字节）'
}

/** 浏览器本地时区标识，例如 UTC+08:00。 */
export function localTimezoneLabel(now: Date = new Date()): string {
  const offsetMinutes = -now.getTimezoneOffset()
  const sign = offsetMinutes >= 0 ? '+' : '-'
  const abs = Math.abs(offsetMinutes)
  return 'UTC' + sign + String(Math.floor(abs / 60)).padStart(2, '0') + ':' + String(abs % 60).padStart(2, '0')
}

/** 带时区的 ISO 时间 → 本地时间控件的值。 */
export function toLocalDateTimeInput(iso: string): string {
  const date = new Date(iso)
  if (!Number.isFinite(date.getTime())) return ''
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000)
  return local.toISOString().slice(0, 16)
}

/** 本地时间控件的值 → 带显式时区偏移的 ISO 时间字符串。 */
export function fromLocalDateTimeInput(local: string): string {
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2})(?::(\d{2}))?$/.exec(local.trim())
  if (!match) return ''
  const date = new Date(local)
  if (!Number.isFinite(date.getTime())) return ''
  const offsetMinutes = -date.getTimezoneOffset()
  const sign = offsetMinutes >= 0 ? '+' : '-'
  const abs = Math.abs(offsetMinutes)
  return match[1] + ':' + (match[2] ?? '00') + sign + String(Math.floor(abs / 60)).padStart(2, '0') + ':' + String(abs % 60).padStart(2, '0')
}

/** 规则摘要里的时间：转成本地可读文本，并附带时区。 */
export function formatMessageTime(iso: string): string {
  const date = new Date(iso)
  if (!Number.isFinite(date.getTime())) return iso
  const parts = new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hour12: false,
  }).format(date)
  return parts + ' ' + localTimezoneLabel(date)
}

/** 去重（忽略大小写与首尾空白），与后端 Distinct(StringComparer.OrdinalIgnoreCase) 对齐。 */
export function dedupeValues(values: string[]): string[] {
  const seen = new Set<string>()
  const result: string[] = []
  for (const value of values) {
    const trimmed = value.trim()
    if (!trimmed) continue
    const key = trimmed.toLowerCase()
    if (seen.has(key)) continue
    seen.add(key)
    result.push(trimmed)
  }
  return result
}

function actionHeaderError(actions: Array<{ kind: string; values: string[] }>) {
  if (!actions.length) return '请至少添加一个执行动作'
  if (actions.length > 21) return '执行动作最多 21 条'
  return ''
}

/** 单个动作自身的错误，用于就近展示；不含「动作 N：」前缀。 */
export function ruleActionRowError(direction: string, actions: Array<{ kind: string; values: string[] }>, index: number) {
  const action = actions[index]
  if (!action) return ''
  const values = dedupeValues(action.values)
  if (action.kind === 'MoveToFolder') {
    if (actions.filter(candidate => candidate.kind === 'MoveToFolder').length > 1) return '一个规则只能包含一个移动动作'
    if (values.length !== 1) return direction === 'Incoming' ? '请选择一个目标文件夹' : '请选择 Sent 副本的目标文件夹'
  } else if (action.kind === 'ForwardTo') {
    const forwards = actions.filter(candidate => candidate.kind === 'ForwardTo').reduce((sum, candidate) => sum + dedupeValues(candidate.values).length, 0)
    if (!values.length || forwards > 20) return '转发地址总计应为 1–20 个'
    if (values.some(value => !/^[^\s@]+@[^\s@]+$/.test(value))) return '请填写完整邮箱地址'
  } else if (action.kind === 'Reject') {
    if (direction !== 'Outgoing') return '只有发信规则支持拒绝发信'
    if (actions.filter(candidate => candidate.kind === 'Reject').length > 1) return '一个规则只能包含一个拒绝动作'
    if (values.length > 1 || (values[0]?.length ?? 0) > 255) return '拒绝理由最多 255 个字符'
  } else return '不支持的动作类型，请重新选择'
  return ''
}

/** 跨动作的冲突，展示在动作列表下方。 */
export function ruleActionPairError(direction: string, actions: Array<{ kind: string; values: string[] }>) {
  if (direction !== 'Outgoing') return ''
  const hasReject = actions.some(action => action.kind === 'Reject')
  const hasForward = actions.some(action => action.kind === 'ForwardTo')
  return hasReject && hasForward ? '拒绝发信与转发不能同时使用，请删除其中一个动作' : ''
}

export function ruleActionError(direction: string, actions: Array<{ kind: string; values: string[] }>) {
  const header = actionHeaderError(actions)
  if (header) return header
  for (let index = 0; index < actions.length; index += 1) {
    const row = ruleActionRowError(direction, actions, index)
    if (row) return '动作 ' + (index + 1) + '：' + row
  }
  return ruleActionPairError(direction, actions)
}

/** 提交前统一去重，避免界面允许、后端静默合并的差异。 */
export function normalizeConditionsForSave(conditions: Array<{ field: string; operator: string; values: string[] }>) {
  return conditions.map(condition => ({ field: condition.field, operator: condition.operator, values: dedupeValues(condition.values) }))
}

export function normalizeActionsForSave(actions: Array<{ kind: string; values: string[] }>) {
  return actions.map(action => ({ kind: action.kind, values: dedupeValues(action.values) }))
}

/** 收信规则可选动作：移动到文件夹、转发到地址。 */
export const incomingActionItems = [
  { title: '移动到文件夹', value: 'MoveToFolder' },
  { title: '转发到地址', value: 'ForwardTo' },
]

/** 发信规则可选动作：保存 Sent 副本到文件夹、转发到地址、拒绝发信。 */
export const outgoingActionItems = [
  { title: '保存副本到文件夹', value: 'MoveToFolder' },
  { title: '转发到地址', value: 'ForwardTo' },
  { title: '拒绝发信', value: 'Reject' },
]

export function ruleActionItems(direction: string) {
  return direction === 'Outgoing' ? outgoingActionItems : incomingActionItems
}

/** 拒绝发信不需要参数，其余动作必须填写值。 */
export function actionNeedsValues(kind: string) {
  return kind !== 'Reject'
}

export function ruleActionPlaceholder(kind: string) {
  if (kind === 'MoveToFolder') return '文件夹名称，回车可添加多个'
  if (kind === 'ForwardTo') return '转发邮箱，回车可添加多个'
  return '拒绝理由（可留空，默认使用系统文案）'
}

/** 动作展示名；MoveToFolder 在收信/发信规则下语义不同，需要按方向取词。 */
export function ruleActionLabel(kind: string, direction?: string) {
  const items = direction ? ruleActionItems(direction) : [...incomingActionItems, ...outgoingActionItems]
  return items.find(item => item.value === kind)?.title
    ?? [...incomingActionItems, ...outgoingActionItems].find(item => item.value === kind)?.title
    ?? kind
}

/** 匹配值里是否使用了 * 或 ? 通配符。 */
export function hasWildcard(value: string) {
  return value.includes('*') || value.includes('?')
}

/** 下一个可用的动作类型：避免默认生成「拒绝 + 转发」或第二个移动动作。 */
export function nextActionKind(direction: string, actions: Array<{ kind: string; values: string[] }>): string | null {
  const used = new Set(actions.map(action => action.kind))
  const available = ruleActionItems(direction).filter(item => {
    if (item.value === 'MoveToFolder') return !used.has('MoveToFolder')
    if (item.value === 'Reject') return direction === 'Outgoing' && !used.has('Reject') && !used.has('ForwardTo')
    if (item.value === 'ForwardTo') return !used.has('ForwardTo') && !used.has('Reject')
    return false
  })
  return available[0]?.value ?? null
}

/** 规则摘要：带字段名，并按字段类型人性化展示取值。 */
export function ruleConditionSummary(condition: ConditionLike) {
  const field = conditionFieldLabel(condition.field)
  const operator = ruleOperators(condition.field).find(item => item.value === condition.operator)?.title ?? condition.operator
  const wildcard = condition.values.some(hasWildcard) ? '（含通配符）' : ''
  let values = condition.values.join('、')
  if (condition.field === 'HasAttachment') values = condition.values.map(value => /^true$/i.test(value) ? '有附件' : '无附件').join('、')
  if (condition.field === 'MessageSize') values = condition.values.map(formatMessageSize).join('、')
  if (condition.field === 'MessageTime') values = condition.values.map(formatMessageTime).join(' 至 ')
  return field + ' ' + operator + wildcard + '：' + (values || '（未填写）')
}

// PUT is a full replacement; toggling must retain the matching and action data.

export function ruleTogglePayload<T extends { name: string; direction: string; matchMode: string; targetMailboxName: string; isEnabled: boolean; conditions: Array<{ field: string; operator: string; values: string[] }>; actions: Array<{ kind: string; values: string[] }> }>(rule: T) {
  return {
    name: rule.name, direction: rule.direction, matchMode: rule.matchMode,
    targetMailboxName: rule.targetMailboxName, isEnabled: !rule.isEnabled,
    conditions: rule.conditions.map(({ field, operator, values }) => ({ field, operator, values: [...values] })),
    actions: rule.actions.map(({ kind, values }) => ({ kind, values: [...values] })),
  }
}
