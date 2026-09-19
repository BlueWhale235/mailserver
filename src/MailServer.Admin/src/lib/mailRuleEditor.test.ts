import { describe, expect, it } from 'vitest'
import { conditionError, ruleOperators, ruleActionError, ruleTogglePayload } from './mailRuleEditor'
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
  it('rejects duplicate moves, invalid forwarding and cross-direction actions', () => {
    const move = { kind: 'MoveToFolder', values: ['Junk'] }
    expect(ruleActionError('Incoming', [move, move])).toContain('只能包含一个')
    expect(ruleActionError('Incoming', [{ kind: 'ForwardTo', values: ['not-an-email'] }])).toContain('完整邮箱地址')
    expect(ruleActionError('Outgoing', [{ kind: 'ForwardTo', values: ['user@example.com'] }])).toBe('')
    expect(ruleActionError('Outgoing', [move])).toContain('只有收信规则')
    expect(ruleActionError('Incoming', [{ kind: 'Reject', values: [] }])).toContain('只有发信规则')
    expect(ruleActionError('Outgoing', [{ kind: 'Reject', values: [] }])).toBe('')
    expect(ruleActionError('Outgoing', [{ kind: 'Reject', values: ['a', 'b'] }])).toContain('255')
    expect(ruleActionError('Incoming', [move, { kind: 'ForwardTo', values: ['user@example.com'] }])).toBe('')
    expect(ruleActionError('Incoming', [{ kind: 'ForwardTo', values: Array.from({ length: 21 }, (_, i) => `user${i}@example.com`) }])).toContain('1–20')
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
})
