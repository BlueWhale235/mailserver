import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { api } from './lib/api'
import { useAuthStore } from './stores/auth'
import router from './router'

vi.mock('./lib/api', () => ({ api: vi.fn(), setSessionExpiredHandler: vi.fn() }))
vi.mock('./lib/mockApi', () => ({ enablePreviewMode: vi.fn(), isPreviewMode: () => false }))

beforeEach(() => {
  setActivePinia(createPinia())
  vi.mocked(api).mockReset()
})

describe('page authorization guard', () => {
  it('keeps the session and redirects a forbidden direct URL to the access-denied page', async () => {
    vi.mocked(api).mockImplementation(async path => {
      if (path === '/session/me') return { id: 'auditor', userName: 'auditor' } as never
      if (path === '/session/permissions') return {
        fullAccess: false,
        systemPermissions: ['audit.read'],
        allDomainPermissions: [],
        domainPermissions: {},
      } as never
      throw new Error(`unexpected request ${path}`)
    })

    await router.push('/system/settings')
    await router.isReady()
    const auth = useAuthStore()
    expect(router.currentRoute.value.name).toBe('forbidden')
    expect(router.currentRoute.value.query).toMatchObject({ from: '/system/settings', target: '全局设置' })
    expect(auth.session?.userName).toBe('auditor')

    await router.push('/system/audit')
    expect(router.currentRoute.value.name).toBe('system-audit')
  })

  it('enforces the requested domain id for direct domain-detail URLs', async () => {
    const auth = useAuthStore()
    auth.session = { id: 'domain-admin', userName: 'domain-admin' }
    auth.initialized = true
    auth.permissions.domainPermissions = { 'domain.read': ['domain-a'] }

    await router.push('/domains/domain-a')
    expect(router.currentRoute.value.name).toBe('domain-detail')
    await router.push('/domains/domain-b')
    expect(router.currentRoute.value.name).toBe('forbidden')
    expect(router.currentRoute.value.query.target).toBe('域详情')
  })
})


describe('account directory routes', () => {
  it('matches configuration and creation before the domain catch-all', () => {
    expect(router.resolve('/domains/domain-a/accounts/new').name).toBe('account-create')
    expect(router.resolve('/domains/domain-a/accounts/account-a').name).toBe('account-settings')
  })
  it('redirects legacy aliases while preserving search and page', async () => {
    const auth = useAuthStore()
    auth.initialized = true
    auth.session = { id: 'admin', userName: 'admin' }
    auth.permissions.fullAccess = true
    await router.push('/domains/domain-a/aliases?query=sales&page=2')
    expect(router.currentRoute.value.name).toBe('account-directory')
    expect(router.currentRoute.value.query).toEqual({ query: 'sales', page: '2' })
  })
})
