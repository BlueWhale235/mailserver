import { afterEach, describe, expect, it, vi } from 'vitest'

describe('API client', () => {
  it('obtains an authenticated CSRF token for the first write after login', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({ token: 'anonymous-token' }))
      .mockResolvedValueOnce(Response.json({ id: 'admin', userName: 'admin' }))
      .mockResolvedValueOnce(Response.json({ token: 'authenticated-token' }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)
    const { api } = await import('./api')
    await api('/session/login', { method: 'POST', body: '{}' })
    await api('/settings/smtp', { method: 'PUT', body: '{}' })
    expect(fetchMock).toHaveBeenCalledTimes(4)
    expect((fetchMock.mock.calls[1][1].headers as Headers).get('X-CSRF-TOKEN')).toBe('anonymous-token')
    expect((fetchMock.mock.calls[3][1].headers as Headers).get('X-CSRF-TOKEN')).toBe('authenticated-token')
  })

  it('discards the previous identity token after logout', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({ token: 'old-session-token' }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(Response.json({ token: 'anonymous-token' }))
      .mockResolvedValueOnce(Response.json({ id: 'another-admin' }))
    vi.stubGlobal('fetch', fetchMock)
    const { api } = await import('./api')
    await api('/session/logout', { method: 'POST' })
    await api('/session/login', { method: 'POST', body: '{}' })
    expect(fetchMock).toHaveBeenCalledTimes(4)
    expect((fetchMock.mock.calls[3][1].headers as Headers).get('X-CSRF-TOKEN')).toBe('anonymous-token')
  })
  afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.resetModules() })

  it.each([200, 202, 204, 205])('accepts an empty successful %s response', async status => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(Response.json({ token: 'csrf' }))
      .mockResolvedValueOnce(new Response(null, { status })))
    const { api } = await import('./api')
    await expect(api<void>('/tasks/task-1/run', { method: 'POST' })).resolves.toBeUndefined()
  })

  it('preserves JSON returned by an accepted background task', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(Response.json({ token: 'csrf' }))
      .mockResolvedValueOnce(Response.json({ executionId: 'run-123' }, { status: 202 })))
    const { api } = await import('./api')
    await expect(api('/tasks/task-1/run', { method: 'POST' })).resolves.toEqual({ executionId: 'run-123' })
  })

  it('does not hide malformed nonempty JSON responses', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html>Unavailable</html>')))
    const { api } = await import('./api')
    await expect(api('/overview')).rejects.toBeInstanceOf(SyntaxError)
  })

  it('adds the CSRF header to mutating same-origin requests', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ token: 'csrf-123' }), { status: 200 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)
    const { api } = await import('./api')

    await api<void>('/session/logout', { method: 'POST' })

    expect(fetchMock).toHaveBeenCalledTimes(2)
    const request = fetchMock.mock.calls[1]
    expect(request[0]).toBe('/api/v1/session/logout')
    expect((request[1].headers as Headers).get('X-CSRF-TOKEN')).toBe('csrf-123')
    expect(request[1].credentials).toBe('same-origin')
  })

  it('exposes RFC Problem Details as a typed error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      title: 'Validation failed', detail: '域名格式不正确', traceId: 'trace-1'
    }), { status: 400 })))
    const { api } = await import('./api')

    await expect(api('/domains')).rejects.toMatchObject({
      status: 400,
      message: '域名格式不正确',
      problem: { traceId: 'trace-1' }
    })
  })

  it('keeps the browser-generated multipart boundary for FormData uploads', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({ token: 'upload-csrf' }))
      .mockResolvedValueOnce(Response.json({ subject: 'CN=mail.northwind.cn' }))
    vi.stubGlobal('fetch', fetchMock)
    const { api } = await import('./api')

    const form = new FormData()
    form.set('certificate', new File(['pem'], 'chain.pem'))
    await api('/domains/domain-1/certificate/upload', { method: 'POST', body: form })

    expect(fetchMock).toHaveBeenCalledTimes(2)
    const headers = fetchMock.mock.calls[1]?.[1].headers as Headers
    expect(headers.get('Content-Type')).toBeNull()
    expect(headers.get('X-CSRF-TOKEN')).toBe('upload-csrf')
    expect(fetchMock.mock.calls[1]?.[1].body).toBe(form)
  })

  it('downloads a protected export with CSRF and preserves the server filename', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({ token: 'export-csrf' }))
      .mockResolvedValueOnce(new Response('{"records":1}', {
        status: 200,
        headers: {
          'Content-Type': 'application/json',
          'Content-Disposition': "attachment; filename*=UTF-8''audit-%E5%AE%A1%E8%AE%A1.json",
        },
      }))
    vi.stubGlobal('fetch', fetchMock)
    const { apiDownload } = await import('./api')

    const result = await apiDownload('/audit/export', { method: 'POST', body: JSON.stringify({ ids: ['audit-1'] }) })

    expect(result.fileName).toBe('audit-审计.json')
    expect(await result.blob.text()).toBe('{"records":1}')
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/v1/audit/export')
    expect((fetchMock.mock.calls[1]?.[1].headers as Headers).get('X-CSRF-TOKEN')).toBe('export-csrf')
  })
})
