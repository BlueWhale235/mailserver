import { describe, expect, it } from 'vitest'
import { prepareDomainDnsSettings, validateDomainDnsSettings, type DomainDnsSettingsModel } from './domainDns'

const defaults = (): DomainDnsSettingsModel => ({
  spfIncludeMx: true,
  spfIncludeA: false,
  spfIpv4: '',
  spfIpv6: '',
  spfIncludes: '',
  spfCustomMechanisms: '',
  spfAllPolicy: '-all',
  dmarcPolicy: 'none',
  dmarcSubdomainPolicy: null,
  dmarcPercentage: 100,
  dmarcAggregateReportUris: '',
  dmarcForensicReportUris: '',
  dmarcDkimAlignment: 'r',
  dmarcSpfAlignment: 'r',
  dmarcFailureOptions: '',
  mtaStsMode: 'none',
  mtaStsPolicyId: '',
  mtaStsMaxAgeSeconds: Number.NaN,
  mtaStsMxPatterns: '',
  tlsReportUris: '',
  daneEnabled: false,
  daneTlsaRecords: '',
})

describe('DNS security form requirements', () => {
  it('accepts blank optional fields when MTA-STS and DANE are disabled', () => {
    const settings = defaults()
    expect(validateDomainDnsSettings(settings)).toBe('')
    const payload = prepareDomainDnsSettings(settings)
    expect(payload.mtaStsPolicyId).toBe('1')
    expect(payload.mtaStsMaxAgeSeconds).toBe(604_800)
    expect(settings.mtaStsPolicyId).toBe('')
  })

  it('requires MX and a valid policy ID only after enabling MTA-STS', () => {
    const settings = defaults()
    settings.mtaStsMode = 'testing'
    expect(validateDomainDnsSettings(settings)).toContain('策略 ID')
    settings.mtaStsPolicyId = '20260925'
    settings.mtaStsMaxAgeSeconds = 604_800
    expect(validateDomainDnsSettings(settings)).toContain('MX 模式')
    settings.mtaStsMxPatterns = 'mail.example.test'
    expect(validateDomainDnsSettings(settings)).toBe('')
  })

  it('requires TLSA records only when DANE is enabled', () => {
    const settings = defaults()
    settings.daneEnabled = true
    expect(validateDomainDnsSettings(settings)).toContain('TLSA 记录')
    settings.daneTlsaRecords = '3 1 1 abcdef'
    expect(validateDomainDnsSettings(settings)).toBe('')
  })
})
