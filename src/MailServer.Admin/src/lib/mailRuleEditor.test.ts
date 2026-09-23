import { describe, expect, it } from 'vitest'
import {
  bytesFromSizeInput,
  conditionError,
  dedupeValues,
  formatMessageSize,
  fromLocalDateTimeInput,
  nextActionKind,
  normalizeActionsForSave,
  normalizeConditionsForSave,
  ruleActionError,
  ruleActionLabel,
  ruleActionPairError,
  ruleActionRowError,
  ruleConditionSummary,
  ruleOperators,
  ruleTogglePayload,
  sizeUnitOptions,
  splitMessageSize,
  toLocalDateTimeInput,
} from './mailRuleEditor'
import { reactive } from 'vue'

describe('mail rule editor contract', () => {
  it('preserves the full rule when toggling a reactive UI record', () => {
    const rule = reactive({ name: '归档', direction: 'Outgoing', matchMode: 'Any', targetMailboxName: 'Sent', isEnabled: true,
      conditions: [{ field: 'Subject', operator: 'ContainsAny', values: ['invoice'] }], actions: [{ kind: 'MoveToFolder', values: ['Sent'] }] })
    const payload = ruleTogglePayload(rule)
    expect(payload).toEqual({ ...rule, isEnabled: false })
    payload.conditions[0]!.values.push('new')
    expect(rule.conditions[0]!.values).toEqual(['invoice'])
    expect(rule.isEnabled).toBe(true)
  })
  it('supports attachment and time conditions returned by the server', () => {
    expect(conditionError({ field: 'HasAttachment', operator: 'Is', values: ['true'] })).toBe('')
    expect(conditionError({ field: 'MessageTime', operator: 'Before', values: ['2026-09-19T08:00:00Z'] })).toBe('')
    expect(conditionError({ field: 'MessageTime', operator: 'Between', values: ['2026-09-20T08:00:00Z', '2026-09-19T08:00:00Z'] })).not.toBe('')
    expect(conditionError({ field: 'HasAttachment', operator: 'Is', values: ['yes'] })).not.toBe('')
  })
  it('validates duplicate moves, invalid forwarding and cross-direction actions', () => {
    const move = { kind: 'MoveToFolder', values: ['Junk'] }
    expect(ruleActionError('Incoming', [move, move])).toContain('只能包含一个')
    expect(ruleActionError('Incoming', [{ kind: 'ForwardTo', values: ['not-an-email'] }])).toContain('完整邮箱地址')
    expect(ruleActionError('Outgoing', [{ kind: 'ForwardTo', values: ['user@example.com'] }])).toBe('')
    // 发信规则允许把 Sent 副本保存到指定文件夹（与后端 MailRuleService 一致）。
    expect(ruleActionError('Outgoing', [move])).toBe('')
    expect(ruleActionError('Outgoing', [{ kind: 'MoveToFolder', values: [] }])).toContain('Sent 副本的目标文件夹')
    expect(ruleActionError('Incoming', [{ kind: 'Reject', values: [] }])).toContain('只有发信规则')
    expect(ruleActionError('Outgoing', [{ kind: 'Reject', values: [] }])).toBe('')
    expect(ruleActionError('Outgoing', [{ kind: 'Reject', values: ['a', 'b'] }])).toContain('255')
    expect(ruleActionError('Incoming', [move, { kind: 'ForwardTo', values: ['user@example.com'] }])).toBe('')
    expect(ruleActionError('Incoming', [{ kind: 'ForwardTo', values: Array.from({ length: 21 }, (_, i) => `user${i}@example.com`) }])).toContain('1–20')
  })

  it('requires at least one action and forbids combining reject with forward', () => {
    expect(ruleActionError('Incoming', [])).toContain('至少添加一个执行动作')
    expect(ruleActionError('Outgoing', [])).toContain('至少添加一个执行动作')
    expect(ruleActionError('Outgoing', [
      { kind: 'Reject', values: ['禁止外发'] },
      { kind: 'ForwardTo', values: ['audit@example.com'] },
    ])).toContain('不能同时使用')
    // 收信规则本来就不允许 Reject，因此不会走到互斥分支。
    expect(ruleActionError('Incoming', [{ kind: 'MoveToFolder', values: ['Junk'] }])).toBe('')
  })
  it('labels move actions by rule direction', () => {
    expect(ruleActionLabel('MoveToFolder', 'Incoming')).toBe('移动到文件夹')
    expect(ruleActionLabel('MoveToFolder', 'Outgoing')).toBe('保存副本到文件夹')
    expect(ruleActionLabel('Reject', 'Outgoing')).toBe('拒绝发信')
  })
  it('does not offer unsupported regex or text comparisons for sizes', () => {
    expect(ruleOperators('Subject').some(item => item.value === 'Regex')).toBe(false)
    expect(ruleOperators('MessageSize').map(item => item.value)).toEqual(['GreaterThan', 'GreaterThanOrEqual', 'LessThan', 'LessThanOrEqual'])
  })
  it('rejects empty conditions and incompatible operators', () => {
    expect(conditionError({ field: 'Sender', operator: 'EqualsAny', values: [' '] })).not.toBe('')
    expect(conditionError({ field: 'MessageSize', operator: 'ContainsAny', values: ['10'] })).not.toBe('')
  })
  it('validates byte counts without floating point truncation', () => {
    for (const value of ['-1', '1.5', 'abc', '9223372036854775808']) {
      expect(conditionError({ field: 'MessageSize', operator: 'GreaterThan', values: [value] })).not.toBe('')
    }
    expect(conditionError({ field: 'MessageSize', operator: 'GreaterThan', values: ['1048576'] })).toBe('')
  })

  it('converts a size amount and unit into exact bytes', () => {
    expect(bytesFromSizeInput('10', 'MiB')).toBe('10485760')
    expect(bytesFromSizeInput('2', 'GiB')).toBe('2147483648')
    expect(bytesFromSizeInput('1.5', 'KiB')).toBe('1536')
    expect(bytesFromSizeInput('0', 'B')).toBe('0')
    // 非法或溢出输入必须被拒绝，而不是静默截断。
    for (const [amount, unit] of [['', 'MiB'], ['abc', 'MiB'], ['-1', 'MiB'], ['1.2.3', 'B'], ['99999999999999999999', 'GiB']]) {
      expect(bytesFromSizeInput(amount!, unit!)).toBe('')
    }
    expect(conditionError({ field: 'MessageSize', operator: 'GreaterThan', values: [bytesFromSizeInput('10', 'MiB')] })).toBe('')
  })

  it('round-trips a byte count through the size editor', () => {
    expect(splitMessageSize('1048576')).toEqual({ amount: '1', unit: 'MiB' })
    expect(splitMessageSize('1536')).toEqual({ amount: '1536', unit: 'B' })
    expect(splitMessageSize('2097152')).toEqual({ amount: '2', unit: 'MiB' })
    expect(formatMessageSize('1048576')).toBe('1 MiB（1048576 字节）')
    expect(formatMessageSize('512')).toBe('512 字节')
    expect(sizeUnitOptions.map(option => option.value)).toEqual(['B', 'KiB', 'MiB', 'GiB'])
  })

  it('round-trips local datetime input with an explicit offset', () => {
    const local = '2026-09-19T08:30'
    const iso = fromLocalDateTimeInput(local)
    expect(iso).toMatch(/^2026-09-19T08:30:00[+-]\d{2}:\d{2}$/)
    expect(toLocalDateTimeInput(iso)).toBe(local)
    expect(conditionError({ field: 'MessageTime', operator: 'Before', values: [iso] })).toBe('')
    expect(fromLocalDateTimeInput('not-a-date')).toBe('')
    expect(conditionError({ field: 'MessageTime', operator: 'Before', values: [] })).toBe('请选择时间')
    expect(conditionError({ field: 'MessageTime', operator: 'Between', values: [iso] })).toBe('请选择开始与结束时间')
  })

  it('dedupes values case-insensitively like the backend', () => {
    expect(dedupeValues([' User@Example.com ', 'user@example.com', 'other@example.com'])).toEqual(['User@Example.com', 'other@example.com'])
    expect(normalizeActionsForSave([{ kind: 'ForwardTo', values: ['a@b.com', 'A@B.com', ''] }])).toEqual([{ kind: 'ForwardTo', values: ['a@b.com'] }])
    expect(normalizeConditionsForSave([{ field: 'Sender', operator: 'EqualsAny', values: ['A', 'a'] }])).toEqual([{ field: 'Sender', operator: 'EqualsAny', values: ['A'] }])
    // 去重后转发总数应以后端一致的数量判定。
    expect(ruleActionError('Incoming', [{ kind: 'ForwardTo', values: Array.from({ length: 21 }, () => 'same@example.com') }])).toBe('')
    expect(ruleActionError('Incoming', [{ kind: 'ForwardTo', values: Array.from({ length: 21 }, (_, i) => `user${i}@example.com`) }])).toContain('1–20')
  })

  it('reports action errors next to the offending row', () => {
    const actions = [
      { kind: 'MoveToFolder', values: ['Junk'] },
      { kind: 'ForwardTo', values: ['not-an-email'] },
    ]
    expect(ruleActionRowError('Incoming', actions, 0)).toBe('')
    expect(ruleActionRowError('Incoming', actions, 1)).toContain('完整邮箱地址')
    expect(ruleActionRowError('Outgoing', [{ kind: 'Reject', values: [] }, { kind: 'ForwardTo', values: ['a@b.com'] }], 1)).toBe('')
    expect(ruleActionPairError('Outgoing', [{ kind: 'Reject', values: [] }, { kind: 'ForwardTo', values: ['a@b.com'] }])).toContain('不能同时使用')
    expect(ruleActionPairError('Incoming', [])).toBe('')
    // 聚合错误仍带动作序号，供无逐行介面的调用方使用。
    expect(ruleActionError('Incoming', actions)).toContain('动作 2：')
  })

  it('picks a non-conflicting default action kind', () => {
    expect(nextActionKind('Incoming', [])).toBe('MoveToFolder')
    expect(nextActionKind('Incoming', [{ kind: 'MoveToFolder', values: [] }])).toBe('ForwardTo')
    expect(nextActionKind('Outgoing', [{ kind: 'Reject', values: [] }])).toBe('MoveToFolder')
    expect(nextActionKind('Outgoing', [{ kind: 'ForwardTo', values: [] }])).toBe('MoveToFolder')
    // 拒绝与转发互斥（后端 ValidateActions 同样拒绝两者并存），且移动动作只能有一个。
    expect(nextActionKind('Outgoing', [{ kind: 'MoveToFolder', values: [] }, { kind: 'Reject', values: [] }])).toBeNull()
    expect(nextActionKind('Outgoing', [{ kind: 'MoveToFolder', values: [] }, { kind: 'ForwardTo', values: [] }])).toBeNull()
    expect(nextActionKind('Incoming', [{ kind: 'MoveToFolder', values: [] }, { kind: 'ForwardTo', values: [] }])).toBeNull()
  })

  it('summarises conditions with the field name and human-readable values', () => {
    expect(ruleConditionSummary({ field: 'Subject', operator: 'ContainsAny', values: ['*invoice*'] })).toBe('主题 包含任意一项（含通配符）：*invoice*')
    expect(ruleConditionSummary({ field: 'HasAttachment', operator: 'Is', values: ['true'] })).toBe('是否有附件 是否包含附件：有附件')
    expect(ruleConditionSummary({ field: 'MessageSize', operator: 'GreaterThan', values: ['1048576'] })).toBe('邮件大小 大于：1 MiB（1048576 字节）')
    expect(ruleConditionSummary({ field: 'Sender', operator: 'EqualsAny', values: [] })).toContain('（未填写）')
  })
})
