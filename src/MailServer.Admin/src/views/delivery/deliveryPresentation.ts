import type { DeliveryStatus, TlsReportStatus } from './types'

export function deliveryStatusPresentation(value: DeliveryStatus) {
  if (value === 'Delivered') return { label: '已投递', tone: 'success' as const }
  if (value === 'Failed') return { label: '永久失败', tone: 'danger' as const }
  if (value === 'Deferred') return { label: '延迟重试', tone: 'warning' as const }
  if (value === 'Processing') return { label: '投递中', tone: 'neutral' as const }
  return { label: '等待投递', tone: 'neutral' as const }
}

export function tlsReportStatusPresentation(value: TlsReportStatus) {
  if (value === 'Completed') return { label: '已送达', tone: 'success' as const }
  if (value === 'Failed') return { label: '重试耗尽', tone: 'danger' as const }
  if (value === 'Retrying') return { label: '等待重试', tone: 'warning' as const }
  return { label: '待发送', tone: 'neutral' as const }
}

export function formatDate(value?: string) {
  return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
}

export function shortId(value: string) {
  return `${value.slice(0, 8)}…${value.slice(-4)}`
}

export const stageLabels: Record<string, string> = {
  DNS: 'DNS 路由',
  CONNECT: '建立连接',
  TLS: 'TLS 协商',
  SMTP: 'SMTP 会话',
}

