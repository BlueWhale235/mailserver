import { api, jsonBody } from './api'

/** Matches MailServer.Domain.DomainDnsSettings. */
export interface DomainDnsSettingsModel {
  domainId?: string
  spfIncludeMx: boolean
  spfIncludeA: boolean
  spfIpv4: string
  spfIpv6: string
  spfIncludes: string
  spfCustomMechanisms: string
  spfAllPolicy: string
  dmarcPolicy: string
  dmarcSubdomainPolicy: string | null
  dmarcPercentage: number
  dmarcAggregateReportUris: string
  dmarcForensicReportUris: string
  dmarcDkimAlignment: string
  dmarcSpfAlignment: string
  dmarcFailureOptions: string
  mtaStsMode: string
  mtaStsPolicyId: string
  mtaStsMaxAgeSeconds: number
  mtaStsMxPatterns: string
  tlsReportUris: string
  daneEnabled: boolean
  daneTlsaRecords: string
  updatedAt?: string
}

export interface DomainDnsRecord {
  name: string
  type: string
  value: string
  zoneFileValue: string
}

export interface DomainDnsConfiguration {
  settings: DomainDnsSettingsModel
  records: DomainDnsRecord[]
}

export interface DomainDnsValidationItem {
  kind: string
  name: string
  expected: string
  actual: string[]
  status: string
}

export interface DomainDnsCheck {
  domainId: string
  status: 'PendingConfiguration' | 'Checking' | 'Healthy' | 'Unhealthy'
  validation: DomainDnsValidation | null
  lastError?: string
  checkedAt?: string
}

export interface DomainDnsValidation {
  domain: string
  items: DomainDnsValidationItem[]
  warnings: string[]
}

export const spfAllPolicyOptions = [
  { title: '-all（仅允许列表内主机）', value: '-all' },
  { title: '~all（软失败，推荐过渡）', value: '~all' },
  { title: '?all（中立）', value: '?all' },
  { title: '+all（全部放行，不推荐）', value: '+all' },
]

export const dmarcPolicyOptions = [
  { title: 'none（仅监控）', value: 'none' },
  { title: 'quarantine（隔离）', value: 'quarantine' },
  { title: 'reject（拒收）', value: 'reject' },
]

export const dmarcSubdomainPolicyOptions = [
  { title: '继承主域策略', value: '' },
  ...dmarcPolicyOptions,
]

export const alignmentOptions = [
  { title: 'relaxed（宽松）', value: 'r' },
  { title: 'strict（严格）', value: 's' },
]

export const mtaStsModeOptions = [
  { title: 'none（不发布）', value: 'none' },
  { title: 'testing（测试）', value: 'testing' },
  { title: 'enforce（强制）', value: 'enforce' },
]

/** Builds a SPF record string the same way the server does, for live preview. */
export function buildSpfValue(settings: DomainDnsSettingsModel): string {
  const parts = ['v=spf1']
  if (settings.spfIncludeMx) parts.push('mx')
  if (settings.spfIncludeA) parts.push('a')
  for (const value of splitList(settings.spfIpv4)) parts.push(`ip4:${value}`)
  for (const value of splitList(settings.spfIpv6)) parts.push(`ip6:${value}`)
  for (const value of splitList(settings.spfIncludes)) parts.push(`include:${value}`)
  for (const value of splitList(settings.spfCustomMechanisms)) parts.push(value)
  parts.push(settings.spfAllPolicy || '~all')
  return parts.join(' ')
}

export function splitList(value: string | null | undefined): string[] {
  return (value ?? '')
    .split(/[\s,;]+/)
    .map(item => item.trim())
    .filter(Boolean)
}

export function dkimPublicRecord(domainName: string, selector: string, publicKey: string): string {
  return `${selector}._domainkey.${domainName} TXT "v=DKIM1; k=rsa; p=${publicKey}"`
}

export function fetchDomainDns(domainId: string) {
  return api<DomainDnsConfiguration>(`/domains/${encodeURIComponent(domainId)}/dns`)
}

export function saveDomainDns(domainId: string, settings: DomainDnsSettingsModel) {
  return api<DomainDnsConfiguration>(`/domains/${encodeURIComponent(domainId)}/dns`, {
    method: 'PUT',
    body: jsonBody(settings),
  })
}

export function generateDomainDkim(domainId: string, selector: string) {
  return api<{ publicKey: string }>(`/domains/${encodeURIComponent(domainId)}/dkim`, {
    method: 'POST',
    body: jsonBody({ selector }),
  })
}

export function validatePublishedDns(domainId: string) {
  return api<DomainDnsValidation>(`/domains/${encodeURIComponent(domainId)}/dns/validate`, { method: 'POST' })
}
