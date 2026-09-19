import { useAuthStore } from '../stores/auth'

export function usePermission() {
  const auth = useAuthStore()

  function can(code?: string, domainId?: string): boolean {
    if (!code) return true
    return auth.can(code, domainId)
  }

  return { can }
}