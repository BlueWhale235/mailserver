import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useAuthStore } from './auth'

describe('permission scope', () => {
  beforeEach(() => setActivePinia(createPinia()))
  it('allows managers to read without granting write access to readers', () => {
    const auth = useAuthStore()
    auth.permissions.systemPermissions = ['settings.manage', 'queue.read']
    expect(auth.can('settings.read')).toBe(true)
    expect(auth.can('queue.manage')).toBe(false)
    expect(auth.can('dashboard.read')).toBe(false)
  })
  it('clearSession wipes the session and every permission without a network call', () => {
    const auth = useAuthStore()
    auth.session = { id: 'admin', userName: 'admin' }
    auth.permissions.fullAccess = true
    auth.permissions.systemPermissions = ['settings.manage']
    auth.permissions.allDomainPermissions = ['domain.read']
    auth.permissions.domainPermissions = { 'domain.manage': ['domain-a'] }

    auth.clearSession()

    expect(auth.session).toBeNull()
    expect(auth.initialized).toBe(true)
    expect(auth.permissions).toEqual({
      fullAccess: false,
      systemPermissions: [],
      allDomainPermissions: [],
      domainPermissions: {},
    })
    expect(auth.permissionsLoaded).toBe(false)
  })

  it('keeps domain and quarantine grants inside the authorized domain', () => {
    const auth = useAuthStore()
    auth.permissions.domainPermissions = { 'domain.manage': ['domain-a'] }
    expect(auth.can('domain.read', 'domain-a')).toBe(true)
    expect(auth.can('domain.quarantine.read', 'domain-a')).toBe(true)
    expect(auth.can('domain.quarantine.manage', 'domain-a')).toBe(true)
    expect(auth.can('domain.read', 'domain-b')).toBe(false)
    expect(auth.can('domain.read')).toBe(false)
    expect(auth.canAnyDomain('domain.read')).toBe(true)
    expect(auth.canAnyDomain('domain.quarantine.read')).toBe(true)
    expect(auth.can('settings.manage')).toBe(false)
  })

  it('does not treat an empty domain grant or an unrelated system grant as domain access', () => {
    const auth = useAuthStore()
    auth.permissions.systemPermissions = ['domain.read']
    auth.permissions.domainPermissions = { 'domain.manage': [] }
    expect(auth.canAnyDomain('domain.read')).toBe(false)
    auth.permissions.allDomainPermissions = ['domain.manage']
    expect(auth.canAnyDomain('domain.read')).toBe(true)
  })
})
