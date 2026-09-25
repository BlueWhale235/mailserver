export interface DirectoryContext {
  domainName: string
  canReadDomain: boolean
  canReadAccounts: boolean
  canReadAliases: boolean
  canManageAccounts: boolean
  canManageAliases: boolean
}
export interface DirectoryAccount {
  id: string
  address: string
  isEnabled: boolean | null
  quotaBytes: number | null
  usedBytes: number | null
  aliasCount: number | null
  matchedAlias: string | null
  matchedAliasCount: number
}
export interface DirectoryAlias {
  id: string
  source: string
  targetAccountId: string | null
  targetAddress: string | null
  mode: 'SendReceive' | 'ReceiveOnly' | 'Disabled'
  canReceive: boolean
  canSend: boolean
  unavailableReason: string | null
}
export interface DirectoryPage<T> { items: T[]; totalCount: number; page: number; pageSize: number }
export function storageSize(bytes: number | null) {
  if (bytes === null) return '—'
  return bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(1)} GiB` : `${(bytes / 1024 ** 2).toFixed(1)} MiB`
}
