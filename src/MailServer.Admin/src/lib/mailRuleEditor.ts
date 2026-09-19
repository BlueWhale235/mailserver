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

export function conditionError(condition: { field: string; operator: string; values: string[] }) {
  if (!ruleOperators(condition.field).some(item => item.value === condition.operator)) return '请选择该字段支持的运算符'
  const values = condition.values.map(value => value.trim()).filter(Boolean)
  if (!values.length || values.length > 50) return '请填写 1–50 个匹配值'
  if (condition.field === 'MessageSize' && (values.length !== 1 || !/^\d+$/.test(values[0]!) || BigInt(values[0]!) > 9223372036854775807n)) return '邮件大小请输入一个非负整数字节数'
  if (condition.field === 'HasAttachment' && (values.length !== 1 || !/^(true|false)$/i.test(values[0]!))) return '附件条件请填写 true（有）或 false（无）'
  if (condition.field === 'MessageTime') {
    const count = condition.operator === 'Between' ? 2 : 1
    if (values.length !== count || values.some(value => !/(Z|[+-]\d{2}:\d{2})$/i.test(value) || !Number.isFinite(Date.parse(value)))) return `请填写 ${count} 个带时区的时间，例如 2026-09-19T08:00:00+08:00`
    if (count === 2 && Date.parse(values[0]!) > Date.parse(values[1]!)) return '开始时间不能晚于结束时间'
  }
  return ''
}

export function ruleActionError(direction: string, actions: Array<{ kind: string; values: string[] }>) {
  if (actions.length > 21) return '执行动作最多 21 条'
  let moves = 0
  let forwards = 0
  let rejects = 0
  for (const [index, action] of actions.entries()) {
    const values = [...new Set(action.values.map(value => value.trim()).filter(Boolean))]
    const prefix = `动作 ${index + 1}：`
    if (action.kind === 'MoveToFolder') {
      if (direction !== 'Incoming') return prefix + '只有收信规则支持移动到文件夹'
      if (++moves > 1) return prefix + '一个规则只能包含一个移动动作'
      if (values.length !== 1) return prefix + '请选择一个目标文件夹'
    } else if (action.kind === 'ForwardTo') {
      forwards += values.length
      if (!values.length || forwards > 20) return prefix + '转发地址总计应为 1–20 个'
      if (values.some(value => !/^[^\s@]+@[^\s@]+$/.test(value))) return prefix + '请填写完整邮箱地址'
    } else if (action.kind === 'Reject') {
      if (direction !== 'Outgoing') return prefix + '只有发信规则支持拒绝发信'
      if (++rejects > 1) return prefix + '一个规则只能包含一个拒绝动作'
      if (values.length > 1 || (values[0]?.length ?? 0) > 255) return prefix + '拒绝理由最多 255 个字符'
    } else return prefix + '不支持的动作类型，请重新选择'
  }
  return ''
}


/** 收信规则可选动作：移动到文件夹、转发到地址。 */
export const incomingActionItems = [
  { title: '移动到文件夹', value: 'MoveToFolder' },
  { title: '转发到地址', value: 'ForwardTo' },
]

/** 发信规则可选动作：转发到地址、拒绝发信。 */
export const outgoingActionItems = [
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

export function ruleActionLabel(kind: string) {
  return [...incomingActionItems, ...outgoingActionItems].find(item => item.value === kind)?.title ?? kind
}

/** 匹配值里是否使用了 * 或 ? 通配符。 */
export function hasWildcard(value: string) {
  return value.includes('*') || value.includes('?')
}

export function ruleConditionSummary(condition: { field: string; operator: string; values: string[] }) {
  const operator = ruleOperators(condition.field).find(item => item.value === condition.operator)?.title ?? condition.operator
  const wildcard = condition.values.some(hasWildcard) ? '（含通配符）' : ''
  return `${operator}${wildcard}：${condition.values.join('、') || '（未填写）'}`
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
