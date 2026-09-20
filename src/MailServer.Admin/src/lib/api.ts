import { isPreviewMode, mockApi, mockApiDownload } from './mockApi'

export interface ApiProblem {
  title?: string
  detail?: string
  status?: number
  traceId?: string
  errors?: Record<string, string[]>
}

/** 错误分类：网络、会话过期、无权限、CSRF、字段校验与一般服务端错误。 */
export type ApiErrorKind = 'network' | 'session-expired' | 'forbidden' | 'csrf' | 'validation' | 'server' | 'problem'

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem: ApiProblem,
    public readonly kind: ApiErrorKind = classifyError(status, problem),
  ) {
    super(problem.detail || problem.title || `请求失败 (${status})`)
  }
}

function classifyError(status: number, problem: ApiProblem): ApiErrorKind {
  if (status === 0) return 'network'
  if (status === 401) return 'session-expired'
  if (status === 403) {
    const text = `${problem.title ?? ''} ${problem.detail ?? ''}`.toLowerCase()
    return text.includes('csrf') || text.includes('antiforgery') ? 'csrf' : 'forbidden'
  }
  if (status === 400 && problem.errors) return 'validation'
  if (status >= 500) return 'server'
  return 'problem'
}

let csrfToken: string | null = null

/** 网络层失败与后端返回的错误统一成 ApiError，调用方无需区分异常类型。 */
async function request(input: string, init?: RequestInit): Promise<Response> {
  try {
    return await fetch(input, init)
  } catch {
    throw new ApiError(0, {
      title: '无法连接到服务器',
      detail: '网络连接失败，请检查网络后重试；本次操作未提交。',
    }, 'network')
  }
}

type SessionExpiredHandler = (problem: ApiProblem) => void
let sessionExpiredHandler: SessionExpiredHandler | null = null
let sessionExpiredNotified = false

/** 注册会话失效处理（清空会话并跳转登录）。并发 401 只会触发一次。 */
export function setSessionExpiredHandler(handler: SessionExpiredHandler | null) {
  sessionExpiredHandler = handler
}

/** 登录成功后重置去重标记，让新会话再次过期时仍能触发处理。 */
export function resetSessionExpiryState() {
  sessionExpiredNotified = false
}

function notifySessionExpired(problem: ApiProblem) {
  if (sessionExpiredNotified) return
  sessionExpiredNotified = true
  sessionExpiredHandler?.(problem)
}

async function ensureCsrf(): Promise<string> {
  if (csrfToken) return csrfToken
  const response = await request('/api/v1/session/csrf', { credentials: 'same-origin' })
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
  const response = await request(`/api/v1${path}`, { ...init, headers, credentials: 'same-origin' })
  if (!response.ok) {
    if (response.status === 400 || response.status === 403) csrfToken = null
    const problem = await readProblem(response)
    // 401 仅代表会话失效；403/CSRF 失败保留登录态，由调用方提示无权限。
    if (response.status === 401) {
      csrfToken = null
      notifySessionExpired(problem)
    }
    throw new ApiError(response.status, problem)
  }
  // Antiforgery tokens are bound to the identity that requested them.
  if (method === 'POST' && (path === '/session/login' || path === '/session/logout')) {
    csrfToken = null
    resetSessionExpiryState()
  }
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
  const response = await request(`/api/v1${path}`, { ...init, headers, credentials: 'same-origin' })
  if (!response.ok) {
    const problem = await readProblem(response)
    if (response.status === 401) {
      csrfToken = null
      notifySessionExpired(problem)
    }
    throw new ApiError(response.status, problem)
  }
  const disposition = response.headers.get('Content-Disposition') ?? ''
  const encodedName = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1]
  const plainName = disposition.match(/filename="?([^";]+)"?/i)?.[1]
  return {
    blob: await response.blob(),
    fileName: encodedName ? decodeURIComponent(encodedName) : plainName || 'download.bin',
  }
}

export const jsonBody = (value: unknown): string => JSON.stringify(value)
