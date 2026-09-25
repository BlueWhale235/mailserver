import { api } from './api'
import type { DomainDnsCheck } from './domainDns'

const cacheLifetimeMs = 12 * 60 * 60 * 1000
const checks = new Map<string, { value: DomainDnsCheck; expiresAt: number }>()

export async function readDomainDnsCheck(domainId: string, refresh = false): Promise<DomainDnsCheck> {
  const cached = checks.get(domainId)
  if (!refresh && cached && cached.expiresAt > Date.now()) return cached.value
  const value = await api<DomainDnsCheck>(`/domains/${domainId}/dns/check`)
  checks.set(domainId, { value, expiresAt: Date.now() + cacheLifetimeMs })
  return value
}

export function rememberDomainDnsCheck(domainId: string, value: DomainDnsCheck): void {
  checks.set(domainId, { value, expiresAt: Date.now() + cacheLifetimeMs })
}
