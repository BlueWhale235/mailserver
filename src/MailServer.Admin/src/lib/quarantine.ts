import { apiDownload } from './api'

export type QuarantineStatus = 'PendingReview' | 'Released' | 'Deleted'
export interface QuarantineRecord {
  id: string; domainId: string; messageId: string; sender: string; recipients: string[]
  remoteAddress: string; pluginId?: string; pluginName?: string; reason: string
  status: QuarantineStatus; quarantinedAt: string; reviewedBy?: string; reviewedAt?: string
  reviewNote?: string; size: number; internetMessageId?: string
}
export interface QuarantinePage { items: QuarantineRecord[]; page: number; pageSize: number; total: number }
export interface QuarantineDetail { record: QuarantineRecord; fileName: string; content: string; truncated: boolean }
export const quarantineStatuses = [
  { title: '待审核', value: 'PendingReview' },
  { title: '已放行', value: 'Released' },
  { title: '已删除', value: 'Deleted' },
]

export async function downloadQuarantineRaw(domainId: string, id: string): Promise<string> {
  const result = await apiDownload(`/domains/${domainId}/quarantine/${id}/raw/download`)
  const href = globalThis.URL.createObjectURL(result.blob)
  const anchor = globalThis.document.createElement('a')
  anchor.href = href
  anchor.download = result.fileName
  anchor.click()
  globalThis.setTimeout(() => globalThis.URL.revokeObjectURL(href), 0)
  return result.fileName
}
