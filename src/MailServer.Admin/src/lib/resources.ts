export interface ResourceProfile {
  id: string
  label: string
  cacheBytes: number
  entryBytes: number
  deliveryConcurrency: number
  smtpConnections: number
  imapConnections: number
}
export interface ResourceValues {
  cacheBytes: number
  entryBytes: number
  deliveryConcurrency: number
  smtpConnections: number
  imapConnections: number
  versionPollSeconds: number
  absoluteRefreshSeconds: number
  maximumConcurrentLoads: number
  ruleFallbackBatchSize: number
}
export interface ResourceSettings { profileId: string | null; profiles: ResourceProfile[]; values: ResourceValues | null }
export interface ResourceUsage {
  profileId: string | null
  cacheBudgetBytes: number
  deliveryLimit: number
  smtpLimit: number
  imapLimit: number
  activeDeliveries: number
  activeSmtp: number
  activeImap: number
}
export interface ResourceProcess {
  processId: string
  instanceName: string
  roles: string[]
  lastSeenAt: string
  estimatedCacheBytes: number
  usage: ResourceUsage | null
  state: 'applied' | 'applying' | 'failed' | 'unreported'
  message: string | null
}
export interface ResourceStatus {
  totalCacheBudgetBytes: number
  totalEstimatedCacheBytes: number
  profileId: string | null
  values: ResourceValues | null
  processes: ResourceProcess[]
}
