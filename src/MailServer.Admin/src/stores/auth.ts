import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api, jsonBody } from '../lib/api'

interface Session {
  id: string
  userName: string
  authenticated?: boolean
  expiresAt?: string
}

export interface PermissionSnapshot {
  fullAccess: boolean
  systemPermissions: string[]
  allDomainPermissions: string[]
  domainPermissions: Record<string, string[]>
}

export const useAuthStore = defineStore('auth', () => {
  const session = ref<Session | null>(null)
  const permissions = ref<PermissionSnapshot>({
    fullAccess: false,
    systemPermissions: [],
    allDomainPermissions: [],
    domainPermissions: {},
  })
  const initialized = ref(false)

  const permissionsLoaded = computed(() => initialized.value && session.value !== null)

  async function initialize() {
    if (initialized.value) return
    try {
      session.value = await api<Session>('/session/me')
      permissions.value = await api<PermissionSnapshot>('/session/permissions')
    } catch {
      session.value = null
      permissions.value = { fullAccess: false, systemPermissions: [], allDomainPermissions: [], domainPermissions: {} }
    }
    initialized.value = true
  }

  function can(code: string, domainId?: string): boolean {
    const snapshot = permissions.value
    if (snapshot.fullAccess) return true
    const canonical = code.trim().toLowerCase()
    const reads = ['domain.read', 'api-key.read', 'admin.account.read', 'firewall.read', 'queue.read', 'settings.read', 'dns.read', 'task.read', 'domain.quarantine.read']
    const candidates = reads.includes(canonical) ? [canonical, canonical.replace(/\.read$/, '.manage')] : [canonical]
    const includes = (grants: string[], value: string) => grants.some(grant => grant.toLowerCase() === value)
    if (domainId) {
      const domainCandidates = candidates.flatMap(value => value.startsWith('domain.quarantine.') ? [value, value.replace('domain.quarantine.', 'domain.')] : [value])
      return domainCandidates.some(value => includes(snapshot.allDomainPermissions, value)
        || Object.entries(snapshot.domainPermissions).some(([key, ids]) => key.toLowerCase() === value && includes(ids, domainId.toLowerCase())))
    }
    return candidates.some(value => includes(snapshot.systemPermissions, value))
  }

  async function login(userName: string, password: string, remember: boolean) {
    session.value = null
    permissions.value = { fullAccess: false, systemPermissions: [], allDomainPermissions: [], domainPermissions: {} }
    const nextSession = await api<Session>('/session/login', {
      method: 'POST',
      body: jsonBody({ userName, password, remember }),
    })
    permissions.value = await api<PermissionSnapshot>('/session/permissions')
    session.value = nextSession
    initialized.value = true
  }

  async function logout() {
    await api<void>('/session/logout', { method: 'POST' })
    session.value = null
    permissions.value = { fullAccess: false, systemPermissions: [], allDomainPermissions: [], domainPermissions: {} }
  }

  return { session, permissions, initialized, permissionsLoaded, initialize, can, login, logout }
})
