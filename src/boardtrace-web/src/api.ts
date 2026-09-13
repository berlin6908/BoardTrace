import type { InspectionDetail, InspectionPage, InspectionQuery } from './inspections'

export interface CurrentUser {
  id: string
  userName: string
  displayName: string
  roles: string[]
  stationId: string | null
}

export const roleLabels: Record<string, string> = {
  Operator: '操作员', ProcessEngineer: '工艺工程师', QualityEngineer: '质量工程师',
}

export class ApiError extends Error {
  constructor(public readonly status: number, message: string) { super(message) }
}

async function requestJson<T>(url: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(url, {
    ...options,
    signal: options.signal ? AbortSignal.any([options.signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000),
    credentials: 'same-origin',
    headers: { Accept: 'application/json', ...options.headers },
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { detail?: string; title?: string } | null
    throw new ApiError(response.status, problem?.detail || problem?.title || `请求失败（${response.status}）`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export function currentUser(signal?: AbortSignal): Promise<CurrentUser> { return requestJson('/api/auth/me', { signal }) }
export function login(userName: string, password: string): Promise<CurrentUser> {
  return requestJson('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ userName, password }) })
}
export function logout(): Promise<void> { return requestJson('/api/auth/logout', { method: 'POST' }) }

export function listInspections(query: InspectionQuery, signal: AbortSignal): Promise<InspectionPage> {
  const params = new URLSearchParams({ page: String(query.page), pageSize: String(query.pageSize) })
  if (query.stationId) params.set('stationId', query.stationId)
  if (query.productId) params.set('productId', query.productId)
  if (query.decision) params.set('decision', query.decision)
  return requestJson(`/api/inspections?${params}`, { signal })
}

export function getInspection(id: string, signal: AbortSignal): Promise<InspectionDetail> {
  return requestJson(`/api/inspections/${encodeURIComponent(id)}`, { signal })
}

export function requestError(error: unknown): string {
  if (error instanceof TypeError) return '无法连接中央服务，请检查连接后重试。'
  if (error instanceof DOMException && error.name === 'TimeoutError') return '中央服务响应超时，请稍后重试。'
  return error instanceof Error ? error.message : '读取失败，请重试。'
}
