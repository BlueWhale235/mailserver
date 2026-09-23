import { describe, expect, it } from 'vitest'
import { canAccessPage, firstAccessiblePath, getPageDefinition } from './pageAccess'

function checker(system: string[] = [], domains: Record<string, string[]> = {}) {
  const normalizedSystem = system.map(value => value.toLowerCase())
  return {
    can(permission: string, domainId?: string) {
      const code = permission.toLowerCase()
      if (!domainId) return normalizedSystem.includes(code)
      return (domains[code] ?? []).some(id => id.toLowerCase() === domainId.toLowerCase())
    },
    canAnyDomain(permission: string) {
      return (domains[permission.toLowerCase()] ?? []).length > 0
    },
  }
}

describe('admin page access catalog', () => {
  it('uses domain scope for the domain list and exact domain scope for a detail route', () => {
    const permissions = checker([], { 'domain.read': ['domain-a'] })
    expect(canAccessPage(getPageDefinition('domains')!, permissions)).toBe(true)
    expect(canAccessPage(getPageDefinition('domain-detail')!, permissions, 'domain-a')).toBe(true)
    expect(canAccessPage(getPageDefinition('domain-detail')!, permissions, 'domain-b')).toBe(false)
    expect(canAccessPage(getPageDefinition('domain-create')!, permissions)).toBe(false)
  })

  it('shows the system landing when at least one system module is accessible', () => {
    const permissions = checker(['audit.read'])
    expect(canAccessPage(getPageDefinition('system')!, permissions)).toBe(true)
    expect(canAccessPage(getPageDefinition('system-audit')!, permissions)).toBe(true)
    expect(canAccessPage(getPageDefinition('system-settings')!, permissions)).toBe(false)
    expect(firstAccessiblePath(permissions)).toBe('/system/audit')
  })

  it('allows mail security for quarantine-only domain access', () => {
    const permissions = checker([], { 'domain.quarantine.read': ['domain-a'] })
    expect(canAccessPage(getPageDefinition('security')!, permissions)).toBe(true)
    expect(canAccessPage(getPageDefinition('quarantine')!, permissions)).toBe(true)
    expect(canAccessPage(getPageDefinition('delivery')!, permissions)).toBe(false)
  })
})
