import { describe, expect, it } from 'vitest'
import { domainNameError, normalizeDomainName } from './domainName'

describe('domain name validation', () => {
  it('normalizes case, surrounding whitespace, and a trailing root dot', () => {
    expect(normalizeDomainName('  Mail.Example.COM. ')).toBe('mail.example.com')
    expect(domainNameError('Mail.Example.COM.')).toBe('')
  })

  it.each([
    '',
    'localhost',
    'a..example',
    '-mail.example',
    'mail-.example',
    'mail_example.com',
    `${'a'.repeat(64)}.example`,
  ])('rejects an invalid hostname: %s', value => {
    expect(domainNameError(value)).not.toBe('')
  })

  it('accepts ascii IDN form and ordinary subdomains', () => {
    expect(domainNameError('xn--fsqu00a.xn--0zwm56d')).toBe('')
    expect(domainNameError('mail.eu.example.com')).toBe('')
  })
})
