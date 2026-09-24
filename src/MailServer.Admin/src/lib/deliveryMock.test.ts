import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { DeliveryAttempt, DeliveryBatchResult, DeliveryJob, DeliverySummary, PagedResult, TlsReportDeliveryDetail } from '../views/delivery/types'

beforeEach(() => vi.resetModules())

describe('delivery preview contract', () => {
  it('returns per-item outcomes and blocks duplicate retry and processing deletion', async () => {
    const { mockApi } = await import('./mockApi')
    const jobs = (await mockApi<PagedResult<DeliveryJob>>('/queues')).items
    const failed = jobs.find(job => job.status === 'Failed')!
    const processing = jobs.find(job => job.status === 'Processing')!
    const first = await mockApi<DeliveryBatchResult>('/queues/batch/retry', {
      method: 'POST', body: JSON.stringify({ ids: [failed.id, processing.id] }),
    })
    expect(first.items.map(item => item.status)).toEqual(['Succeeded', 'Conflict'])
    const repeated = await mockApi<DeliveryBatchResult>('/queues/batch/retry', {
      method: 'POST', body: JSON.stringify({ ids: [failed.id] }),
    })
    expect(repeated.items[0]?.status).toBe('Conflict')
    const deletion = await mockApi<DeliveryBatchResult>('/queues/batch/delete', {
      method: 'POST', body: JSON.stringify({ ids: [failed.id, processing.id] }),
    })
    expect(deletion.items.map(item => item.status)).toEqual(['Succeeded', 'Conflict'])
    expect((await mockApi<PagedResult<DeliveryJob>>('/queues')).items.some(job => job.id === processing.id)).toBe(true)
  })

  it('keeps active, delivered and failed scopes separate and summary consistent', async () => {
    const { mockApi } = await import('./mockApi')
    const active = await mockApi<PagedResult<DeliveryJob>>('/queues?scope=Active')
    const history = await mockApi<PagedResult<DeliveryJob>>('/queues?scope=History')
    const failed = await mockApi<PagedResult<DeliveryJob>>('/queues?scope=Failed')
    const summary = await mockApi<DeliverySummary>('/queues/summary')
    expect(active.items.every(item => item.status !== 'Delivered')).toBe(true)
    expect(history.items.every(item => item.status === 'Delivered')).toBe(true)
    expect(failed.items.every(item => item.status === 'Failed')).toBe(true)
    expect(summary.deliveredLast24Hours).toBe(history.totalCount)
    expect(summary.failed).toBe(failed.totalCount)
    expect(summary.pending + summary.processing + summary.deferred + summary.failed).toBe(active.totalCount)
  })

  it('returns latest attempts first and masks envelope addresses in session summaries', async () => {
    const { mockApi } = await import('./mockApi')
    const jobs = await mockApi<PagedResult<DeliveryJob>>('/queues')
    for (const job of jobs.items) {
      const attempts = await mockApi<DeliveryAttempt[]>(`/queues/${job.id}/attempts`)
      expect(attempts.map(attempt => attempt.attemptNumber)).toEqual(attempts.map(attempt => attempt.attemptNumber).sort((a, b) => b - a))
      for (const attempt of attempts) {
        expect(attempt.sessionSummary ?? '').not.toContain(`<${job.sender}>`)
        expect(attempt.sessionSummary ?? '').not.toContain(`<${job.recipient}>`)
      }
    }
  })

  it('supports report pagination, retry reset and completed-report protection', async () => {
    const { mockApi } = await import('./mockApi')
    const all = await mockApi<PagedResult<TlsReportDeliveryDetail>>('/delivery/tls-reports')
    const first = await mockApi<PagedResult<TlsReportDeliveryDetail>>('/delivery/tls-reports?page=1&pageSize=2')
    const second = await mockApi<PagedResult<TlsReportDeliveryDetail>>('/delivery/tls-reports?page=2&pageSize=2')
    expect(first.totalCount).toBe(all.totalCount)
    expect(first.items).toHaveLength(2)
    expect(new Set([...first.items, ...second.items].map(item => item.id)).size).toBe(4)
    expect(first.items[0]).not.toHaveProperty('reportJson')
    const failed = all.items.find(item => item.status === 'Failed')!
    const key = '7d563ddc-8561-4dfb-97cb-6dc28fed1bce'
    const request = { method: 'POST', headers: { 'Idempotency-Key': key } }
    await expect(mockApi(`/delivery/tls-reports/${failed.id}/retry`, { method: 'POST' })).rejects.toThrow('Idempotency-Key')
    expect(await mockApi(`/delivery/tls-reports/${failed.id}/retry`, request)).toEqual({ scheduled: true, replayed: false })
    expect(await mockApi(`/delivery/tls-reports/${failed.id}/retry`, request)).toEqual({ scheduled: false, replayed: true })
    expect(await mockApi(`/delivery/tls-reports/${failed.id}/retry`, { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() } })).toEqual({ scheduled: false, replayed: false })
    const updated = await mockApi<TlsReportDeliveryDetail>(`/delivery/tls-reports/${failed.id}`)
    expect(updated.status).toBe('Pending')
    expect(updated.attempts).toBe(0)
    expect(updated.lastError).toBeUndefined()
    expect(() => JSON.parse(updated.reportJson)).not.toThrow()
    const completed = all.items.find(item => item.status === 'Completed')!
    await expect(mockApi(`/delivery/tls-reports/${completed.id}/retry`, request)).rejects.toThrow('已完成')
    await expect(mockApi('/delivery/tls-reports/missing/retry', request)).rejects.toThrow('不存在')
  })
})
