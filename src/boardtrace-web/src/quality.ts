import { requestJson } from './api'
import type { Decision, ExecutionStatus, InspectionPurpose } from './inspections'

export type ReviewDisposition = 'Accept' | 'Reject' | 'Rework' | 'ResolveTechnicalIssue'
export interface InspectionReview { inspectionId: string; disposition: ReviewDisposition; note: string; reviewedById: string; reviewedByName: string; reviewedAt: string }
export interface ReworkOrder { id: string; originalInspectionId: string; batchId: string; stationId: string; productId: string; sampleId: string; recipeVersionId: string; recipeBundleHash: string; reason: string; createdById: string; createdByName: string; createdAt: string }
export interface InspectionQualityDetails { review: InspectionReview | null; sourceReworkOrder: ReworkOrder | null; reworkOrder: ReworkOrder | null; reinspectionId: string | null }
export interface QualityQueueItem { inspectionId: string; batchId: string; batchNumber: string; stationId: string; productId: string; sampleId: string; purpose: InspectionPurpose; executionStatus: ExecutionStatus; decision: Decision; startedAt: string }
export interface QualityQueuePage { items: QualityQueueItem[]; total: number; page: number; pageSize: number }
export const dispositionLabels: Record<ReviewDisposition, string> = { Accept: '接受产品', Reject: '拒收产品', Rework: '要求返工或重新检测', ResolveTechnicalIssue: '已处理技术异常' }

export function listQualityQueue(query: { batchId: string; stationId: string; page: number; pageSize: number }, signal: AbortSignal): Promise<QualityQueuePage> {
  const params = new URLSearchParams({ page: String(query.page), pageSize: String(query.pageSize) })
  if (query.batchId.trim()) params.set('batchId', query.batchId.trim())
  if (query.stationId.trim()) params.set('stationId', query.stationId.trim())
  return requestJson(`/api/quality/queue?${params}`, { signal })
}
export function getQualityDetails(id: string, signal: AbortSignal): Promise<InspectionQualityDetails> {
  return requestJson(`/api/quality/inspections/${encodeURIComponent(id)}`, { signal })
}
export function submitReview(id: string, disposition: ReviewDisposition, note: string, signal: AbortSignal): Promise<InspectionQualityDetails> {
  return requestJson(`/api/quality/inspections/${encodeURIComponent(id)}/review`, { method: 'POST', signal,
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ disposition, note }) })
}
