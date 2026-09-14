import { ApiError, requestJson } from './api'
import type { Decision, ExecutionStatus, InspectionPurpose, InspectionRecord } from './inspections'
import type { PublishedRecipeSummary } from './recipes'
import type { InspectionQualityDetails, ReviewDisposition } from './quality'

export type BatchStatus = 'AwaitingFirstArticle' | 'Approved' | 'InProgress' | 'Closed'
export interface CreateBatchRequest { batchNumber: string; productType: string; fieldOfView: string; plannedQuantity: number; stationId: string; recipeVersionId: string }
export interface BatchDefinition extends CreateBatchRequest { id: string; recipeBundleHash: string; createdById: string; createdByName: string; createdAt: string }
export interface FirstArticleApproval { batchId: string; inspectionId: string; approvedById: string; approvedByName: string; approvedAt: string }
export interface BatchInspectionSummary { id: string; productId: string; sourceKind: string; operatorName: string; executionStatus: ExecutionStatus; decision: Decision; startedAt: string }
export interface BatchSummary { batch: BatchDefinition; status: BatchStatus; receivedProductionCount: number }
export interface BatchDetails extends BatchSummary { approval: FirstArticleApproval | null; firstArticles: BatchInspectionSummary[]; technicalFailureCount: number }
export interface StationSummary { stationId: string; displayName: string }
export interface StationRuntimeUpdate { batchId: string | null; archiveId: string | null; firstArticleCount: number; productionCount: number; reinspectionCount: number; pendingUploads: number; hasStartedInspection: boolean; hasUnacknowledgedPlc: boolean; state: string; alarm: string | null; observedAt: string }
export interface StationRuntimeView { stationId: string; displayName: string; runtime: StationRuntimeUpdate | null; receivedAt: string | null; isOnline: boolean; batchNumber: string | null }
export interface BatchCounts { firstArticle: number; production: number; reinspection: number }
export interface BatchClosureAudit { batchId: string; archiveId: string; closedById: string; closedByName: string; closedAt: string }
export interface BatchClosureCheck { batchId: string; status: BatchStatus; canClose: boolean; blockers: string[]; centralCounts: BatchCounts; station: StationRuntimeView | null; unreviewedCount: number; pendingReworkOrders: number; closure: BatchClosureAudit | null }
export interface BatchPurposeStatistics { purpose: InspectionPurpose; attempts: number; completed: number; machinePass: number; machineFail: number; technicalFailures: number }
export interface FirstInspectionYield { evaluatedProducts: number; passedProducts: number; failedProducts: number; passRate: number | null }
export interface BatchSourceStatistics { sourceKind: string; purposes: BatchPurposeStatistics[]; firstInspection: FirstInspectionYield }
export interface ReviewDispositionCount { disposition: ReviewDisposition; count: number }
export interface BatchReportSummary { purposes: BatchPurposeStatistics[]; firstInspection: FirstInspectionYield; bySource: BatchSourceStatistics[]; reviews: ReviewDispositionCount[] }
export interface BatchReportRow { inspection: InspectionRecord; contentHash: string; receivedAt: string; quality: InspectionQualityDetails; testedImageUrl: string | null; referenceImageUrl: string | null }
export interface BatchOperationalReport { batch: BatchDefinition; status: BatchStatus; archiveId: string | null; approval: FirstArticleApproval | null; closure: BatchClosureAudit | null; summary: BatchReportSummary; inspections: BatchReportRow[]; scope: string }

export const batchStatusLabels: Record<BatchStatus, string> = {
  AwaitingFirstArticle: '等待首件', Approved: '首件已批准', InProgress: '生产中', Closed: '已关闭',
}
export function listBatches(signal: AbortSignal): Promise<BatchSummary[]> { return requestJson('/api/batches', { signal }) }
export function getBatch(id: string, signal: AbortSignal): Promise<BatchDetails> { return requestJson(`/api/batches/${encodeURIComponent(id)}`, { signal }) }
export function listStations(signal: AbortSignal): Promise<StationSummary[]> { return requestJson('/api/stations', { signal }) }
export function listStationRuntime(signal: AbortSignal): Promise<StationRuntimeView[]> { return requestJson('/api/stations/runtime', { signal }) }
export function getBatchClosure(id: string, signal: AbortSignal): Promise<BatchClosureCheck> { return requestJson(`/api/batches/${encodeURIComponent(id)}/closure`, { signal }) }
export function getBatchReport(id: string, signal: AbortSignal): Promise<BatchOperationalReport> { return requestJson(`/api/batches/${encodeURIComponent(id)}/report`, { signal }) }
export class CloseBlockedError extends ApiError { constructor(public readonly check: BatchClosureCheck) { super(409, check.blockers.join('；') || '批次当前不能关闭。') } }
export async function closeBatch(id: string, signal: AbortSignal): Promise<BatchClosureAudit> {
  const response = await fetch(`/api/batches/${encodeURIComponent(id)}/close`, {
    method: 'POST', credentials: 'same-origin', headers: { Accept: 'application/json' }, signal: AbortSignal.any([signal, AbortSignal.timeout(15000)]),
  })
  if (response.status === 409) throw new CloseBlockedError(await response.json() as BatchClosureCheck)
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { title?: string; detail?: string } | null
    throw new ApiError(response.status, problem?.detail || problem?.title || `关闭失败（${response.status}）`)
  }
  return response.json() as Promise<BatchClosureAudit>
}
export function createBatch(input: CreateBatchRequest, signal: AbortSignal): Promise<BatchDetails> {
  return requestJson('/api/batches', { method: 'POST', signal, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) })
}
export function approveFirstArticle(batchId: string, inspectionId: string, signal: AbortSignal): Promise<FirstArticleApproval> {
  return requestJson(`/api/batches/${encodeURIComponent(batchId)}/first-article-approval`, {
    method: 'PUT', signal, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ inspectionId }),
  })
}
export type { PublishedRecipeSummary }
