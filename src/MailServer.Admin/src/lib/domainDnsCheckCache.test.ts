import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from './api'
import { readDomainDnsCheck, rememberDomainDnsCheck } from './domainDnsCheckCache'
import type { DomainDnsCheck } from './domainDns'

vi.mock('./api', () => ({ api: vi.fn() }))

beforeEach(() => vi.clearAllMocks())
afterEach(() => vi.restoreAllMocks())

describe('domain DNS check cache', () => {
  it('reads the stored result once per domain for twelve hours', async () => {
    const now = 1_000_000
    vi.spyOn(Date, 'now').mockReturnValue(now)
    const result: DomainDnsCheck = { domainId: 'cache-domain', status: 'Healthy', validation: null }
    vi.mocked(api).mockResolvedValue(result)

    expect(await readDomainDnsCheck('cache-domain')).toBe(result)
    expect(await readDomainDnsCheck('cache-domain')).toBe(result)
    expect(api).toHaveBeenCalledTimes(1)

    await readDomainDnsCheck('cache-domain', true)
    expect(api).toHaveBeenCalledTimes(2)

    vi.spyOn(Date, 'now').mockReturnValue(now + 12 * 60 * 60 * 1000)
    await readDomainDnsCheck('cache-domain')
    expect(api).toHaveBeenCalledTimes(3)
  })

  it('uses a newly completed manual check immediately', async () => {
    const result: DomainDnsCheck = { domainId: 'manual-domain', status: 'Unhealthy', validation: null }
    rememberDomainDnsCheck('manual-domain', result)

    expect(await readDomainDnsCheck('manual-domain')).toBe(result)
    expect(api).not.toHaveBeenCalled()
  })
})
