import type { InspectionDetail, InspectionPage, InspectionQuery } from './inspections'

async function getJson<T>(url: string, signal: AbortSignal): Promise<T> {
  const response = await fetch(url, {
    signal: AbortSignal.any([signal, AbortSignal.timeout(15000)]),
    headers: { Accept: 'application/json' },
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { detail?: string; title?: string } | null
    throw new Error(problem?.detail || problem?.title || `请求失败（${response.status}）`)
  }
  return response.json() as Promise<T>
}

export function listInspections(query: InspectionQuery, signal: AbortSignal): Promise<InspectionPage> {
  const params = new URLSearchParams({ page: String(query.page), pageSize: String(query.pageSize) })
  if (query.stationId) params.set('stationId', query.stationId)
  if (query.productId) params.set('productId', query.productId)
  if (query.decision) params.set('decision', query.decision)
  return getJson(`/api/inspections?${params}`, signal)
}

export function getInspection(id: string, signal: AbortSignal): Promise<InspectionDetail> {
  return getJson(`/api/inspections/${encodeURIComponent(id)}`, signal)
}

export function requestError(error: unknown): string {
  if (error instanceof TypeError) return '无法连接中央服务，请检查连接后重试。'
  if (error instanceof DOMException && error.name === 'TimeoutError') return '中央服务响应超时，请稍后重试。'
  return error instanceof Error ? error.message : '读取失败，请重试。'
}
