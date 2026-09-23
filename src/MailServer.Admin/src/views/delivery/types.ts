export type DeliveryStatus = 'Pending' | 'Processing' | 'Delivered' | 'Deferred' | 'Failed'

export interface DeliveryJob {
  id: string
  sender: string
  recipient: string
  status: DeliveryStatus
  attempts: number
  nextAttemptAt: string
  lastError?: string
  createdAt: string
  updatedAt: string
}

export interface DeliveryBatchResult {
  items: { id: string; status: 'Succeeded' | 'NotFound' | 'Conflict'; detail?: string }[]
  succeeded: number
  failed: number
}

export interface DeliverySummary {
  pending: number
  processing: number
  deferred: number
  failed: number
  deliveredLast24Hours: number
  failedLast24Hours: number
  averageAttemptMillisecondsLast24Hours: number
}

export interface DeliveryAttempt {
  id: string
  attemptNumber: number
  succeeded: boolean
  permanentFailure: boolean
  stage: string
  mxHost?: string
  remoteAddress?: string
  smtpStatusCode?: string
  enhancedStatusCode?: string
  tlsProtocol?: string
  tlsCipherSuite?: string
  durationMilliseconds: number
  failureCategory?: string
  nextRetryAt?: string
  response?: string
  sessionSummary?: string
  createdAt: string
}

export interface DeliveryRawMessage {
  deliveryJobId: string
  fileName: string
  content: string
}

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

export type DeliveryWorkspace = 'queue' | 'history' | 'bounces'

export type TlsReportStatus = 'Pending' | 'Retrying' | 'Failed' | 'Completed'

export interface TlsReportDelivery {
  id: string
  policyDomain: string
  reportUri: string
  periodStart: string
  periodEnd: string
  status: TlsReportStatus
  attempts: number
  nextAttemptAt: string
  completedAt?: string
  lastError?: string
}

export interface TlsReportDeliveryDetail extends TlsReportDelivery {
  reportJson: string
}

export interface MxConnectivityDiagnostic {
  host: string
  address: string
  addressFamily: 'IPv4' | 'IPv6' | string
  port: number
  success: boolean
  error?: string | null
}

export interface DnsDiagnosticResult {
  domain: string
  mx: string[]
  spf: string[]
  dmarc: string[]
  dkim: string[]
  mtaSts: string[]
  tlsRpt: string[]
  tlsa: string[]
  mxConnectivity: MxConnectivityDiagnostic[]
  warnings: string[]
}
