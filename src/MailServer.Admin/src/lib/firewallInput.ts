/** Validate conventional IPv4/IPv6 input; the API remains authoritative. */
export function inspectFirewallLines(value: string) {
  const entries: Array<{ value: string; line: number; normalized: string; blocksAll: boolean }> = []
  const errors: string[] = []
  const seen = new Map<string, number>()
  value.split(/\r?\n/).forEach((text, index) => {
    const input = text.trim()
    if (!input) return
    try {
      const parsed = inspectFirewallInput(input)
      const previous = seen.get(parsed.normalized)
      if (previous !== undefined) { errors.push(`第 ${index + 1} 行与第 ${previous} 行重复`); return }
      seen.set(parsed.normalized, index + 1)
      entries.push({ value: input, line: index + 1, normalized: parsed.normalized, blocksAll: parsed.blocksAll })
    } catch (cause) { errors.push(`第 ${index + 1} 行：${cause instanceof Error ? cause.message : 'IP 格式无效'}`) }
  })
  if (!entries.length && !errors.length) errors.push('请输入至少一个 IP 地址或 CIDR 网段')
  if (entries.length > 100) errors.push('单次最多添加 100 条规则，请分批提交')
  return { entries, errors, blocksAll: entries.some(entry => entry.blocksAll) }
}

export function inspectFirewallInput(value: string) {
  const parts = value.trim().split('/')
  const address = parts[0] ?? ''
  const ipv6 = address.includes(':')
  const maximum = ipv6 ? 128 : 32
  if (!address || parts.length > 2) throw new Error('请输入有效的 IP 地址或 CIDR 网段')
  let normalizedAddress = address
  if (ipv6) {
    if (!/^[0-9a-f:.]+$/i.test(address)) throw new Error('请输入有效的 IPv6 地址')
    try { normalizedAddress = new globalThis.URL(`http://[${address}]/`).hostname.slice(1, -1) }
    catch { throw new Error('请输入有效的 IPv6 地址') }
  } else {
    const octets = address.split('.')
    if (octets.length !== 4 || octets.some(octet => !/^(0|[1-9]\d{0,2})$/.test(octet) || Number(octet) > 255)) {
      throw new Error('请输入完整的 IPv4 地址，例如 192.0.2.10')
    }
  }
  const prefixText = parts[1]
  if (prefixText !== undefined && (!/^\d{1,3}$/.test(prefixText) || Number(prefixText) > maximum)) {
    throw new Error(`CIDR 前缀必须在 0 到 ${maximum} 之间`)
  }
  const prefix = prefixText === undefined ? maximum : Number(prefixText)
  return { normalized: `${normalizedAddress}/${prefix}`, prefix, family: ipv6 ? 'IPv6' : 'IPv4', blocksAll: prefix === 0 }
}
