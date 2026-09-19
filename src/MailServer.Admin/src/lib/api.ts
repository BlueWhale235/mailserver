import { isPreviewMode, mockApi, mockApiDownload } from './mockApi'

export interface ApiProblem {
  title?: string
  detail?: string
  status?: number
  traceId?: string
  errors?: Record<string, string[]>
}

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly problem: ApiProblem) {
    super(problem.detail || problem.title || `请求失败 (${status})`)
  }
}

let csrfToken: string | null = null

async function ensureCsrf(): Promise<string> {
  if (csrfToken) return csrfToken
  const response = await fetch('/api/v1/session/csrf', { credentials: 'same-origin' })
  if (!response.ok) throw new ApiError(response.status, await readProblem(response))
  const payload = await response.json() as { token: string }
  csrfToken = payload.token
  return csrfToken
}

async function readProblem(response: Response): Promise<ApiProblem> {
  try {
    const problem = await response.json() as ApiProblem & { error?: string }
    return { ...problem, detail: problem.detail || problem.error }
  }
  catch { return { status: response.status, title: response.statusText } }
}

// Browsers must set the multipart boundary themselves: forcing application/json
// on a FormData body makes the request unreadable by the server.
function isMultipartBody(body: BodyInit | null | undefined): boolean {
  return typeof FormData !== 'undefined' && body instanceof FormData
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  if (isPreviewMode()) return mockApi<T>(path, init)
  const method = (init.method || 'GET').toUpperCase()
  const headers = new Headers(init.headers)
  if (init.body && !isMultipartBody(init.body) && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) headers.set('X-CSRF-TOKEN', await ensureCsrf())
  const response = await fetch(`/api/v1${path}`, { ...init, headers, credentials: 'same-origin' })
  if (!response.ok) {
    if (response.status === 400 || response.status === 403) csrfToken = null
    throw new ApiError(response.status, await readProblem(response))
  }
  // Antiforgery tokens are bound to the identity that requested them.
  if (method === 'POST' && (path === '/session/login' || path === '/session/logout')) csrfToken = null
  if (response.status === 204) return undefined as T
  // Accepted background actions may have no body; a successful action must not
  // be reported as failed merely because there is no JSON to decode.
  const body = await response.text()
  if (!body.trim()) return undefined as T
  return JSON.parse(body) as T
}

export interface ApiDownload {
  blob: Blob
  fileName: string
}

export async function apiDownload(path: string, init: RequestInit = {}): Promise<ApiDownload> {
  if (isPreviewMode()) return mockApiDownload(path, init)
  const method = (init.method || 'GET').toUpperCase()
  const headers = new Headers(init.headers)
  if (init.body && !isMultipartBody(init.body) && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) headers.set('X-CSRF-TOKEN', await ensureCsrf())
  const response = await fetch(`/api/v1${path}`, { ...init, headers, credentials: 'same-origin' })
  if (!response.ok) throw new ApiError(response.status, await readProblem(response))
  const disposition = response.headers.get('Content-Disposition') ?? ''
  const encodedName = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1]
  const plainName = disposition.match(/filename="?([^";]+)"?/i)?.[1]
  return {
    blob: await response.blob(),
    fileName: encodedName ? decodeURIComponent(encodedName) : plainName || 'download.bin',
  }
}

export const jsonBody = (value: unknown): string => JSON.stringify(value)
