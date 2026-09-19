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
  it('keeps domain and quarantine grants inside the authorized domain', () => {
    const auth = useAuthStore()
    auth.permissions.domainPermissions = { 'domain.manage': ['domain-a'] }
    expect(auth.can('domain.read', 'domain-a')).toBe(true)
    expect(auth.can('domain.quarantine.read', 'domain-a')).toBe(true)
    expect(auth.can('domain.quarantine.manage', 'domain-a')).toBe(true)
    expect(auth.can('domain.read', 'domain-b')).toBe(false)
    expect(auth.can('domain.read')).toBe(false)
    expect(auth.can('settings.manage')).toBe(false)
  })
})
