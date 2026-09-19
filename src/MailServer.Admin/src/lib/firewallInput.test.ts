import { describe, expect, it } from 'vitest'
import { inspectFirewallInput, inspectFirewallLines } from './firewallInput'

describe('firewall address validation', () => {
  it('accepts mixed families one per line and ignores blank lines', () => {
    const result = inspectFirewallLines(' 192.0.2.1 \r\n\r\n2001:db8::/64\n198.51.100.0/24')
    expect(result.errors).toEqual([])
    expect(result.entries.map(entry => entry.line)).toEqual([1, 3, 4])
  })
  it('reports original line numbers and normalized duplicates', () => {
    expect(inspectFirewallLines('192.0.2.1\n\nwrong\n192.0.2.1/32').errors).toEqual([
      expect.stringContaining('第 3 行'), expect.stringContaining('第 4 行与第 1 行重复'),
    ])
  })
  it('rejects empty and oversized batches and flags any all-source rule', () => {
    expect(inspectFirewallLines('\n  ').errors).toHaveLength(1)
    expect(inspectFirewallLines(Array.from({ length: 101 }, (_, index) => `192.0.2.${index}`).join('\n')).errors).toContain('单次最多添加 100 条规则，请分批提交')
    expect(inspectFirewallLines('192.0.2.1\n::/0').blocksAll).toBe(true)
  })
  it.each(['', 'localhost', '127.1', '999.1.1.1', '192.168.1.01', '192.0.2.1/', '192.0.2.1/33', '192.0.2.1/-1', '192.0.2.1/1/2', '2001:::1', '::1/129', '::1/abc', '[::1]', '::1%eth0'])('rejects malformed or ambiguous input %s', value => {
    expect(() => inspectFirewallInput(value)).toThrow()
  })
  it.each([
    [' 192.0.2.10 ', '192.0.2.10/32'],
    ['192.0.2.0/24', '192.0.2.0/24'],
    ['2001:DB8::1', '2001:db8::1/128'],
    ['2001:db8::/64', '2001:db8::/64'],
  ])('accepts conventional addresses %s', (value, normalized) => {
    expect(inspectFirewallInput(value).normalized).toBe(normalized)
  })
  it('flags both all-source prefixes', () => {
    expect(inspectFirewallInput('0.0.0.0/0').blocksAll).toBe(true)
    expect(inspectFirewallInput('::/0').blocksAll).toBe(true)
    expect(inspectFirewallInput('::1').blocksAll).toBe(false)
  })
})
