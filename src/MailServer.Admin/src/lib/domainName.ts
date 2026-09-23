export function normalizeDomainName(value: string): string {
  return value.trim().replace(/\.+$/, '').toLowerCase()
}

export function domainNameError(value: string): string {
  const normalized = normalizeDomainName(value)
  if (!normalized) return '请输入域名'
  if (normalized.length > 253 || !normalized.includes('.')) return '请输入完整域名，例如 example.com'
  const labels = normalized.split('.')
  if (labels.some(label => !/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i.test(label))) {
    return '域名标签只能包含字母、数字和连字符，且不能以连字符开头或结尾'
  }
  return ''
}
