import { requestJson } from './api'
import type { Decision, ExecutionStatus } from './inspections'
import type { PublishedRecipeSummary } from './recipes'

export type BatchStatus = 'AwaitingFirstArticle' | 'Approved' | 'InProgress' | 'Closed'
export interface CreateBatchRequest { batchNumber: string; productType: string; fieldOfView: string; plannedQuantity: number; stationId: string; recipeVersionId: string }
export interface BatchDefinition extends CreateBatchRequest { id: string; recipeBundleHash: string; createdById: string; createdByName: string; createdAt: string }
export interface FirstArticleApproval { batchId: string; inspectionId: string; approvedById: string; approvedByName: string; approvedAt: string }
export interface BatchInspectionSummary { id: string; productId: string; sourceKind: string; operatorName: string; executionStatus: ExecutionStatus; decision: Decision; startedAt: string }
export interface BatchSummary { batch: BatchDefinition; status: BatchStatus; receivedProductionCount: number }
export interface BatchDetails extends BatchSummary { approval: FirstArticleApproval | null; firstArticles: BatchInspectionSummary[]; technicalFailureCount: number }
export interface StationSummary { stationId: string; displayName: string }

export const batchStatusLabels: Record<BatchStatus, string> = {
  AwaitingFirstArticle: '等待首件', Approved: '首件已批准', InProgress: '生产中', Closed: '已关闭',
}
export function listBatches(signal: AbortSignal): Promise<BatchSummary[]> { return requestJson('/api/batches', { signal }) }
export function getBatch(id: string, signal: AbortSignal): Promise<BatchDetails> { return requestJson(`/api/batches/${encodeURIComponent(id)}`, { signal }) }
export function listStations(signal: AbortSignal): Promise<StationSummary[]> { return requestJson('/api/stations', { signal }) }
export function createBatch(input: CreateBatchRequest, signal: AbortSignal): Promise<BatchDetails> {
  return requestJson('/api/batches', { method: 'POST', signal, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) })
}
export function approveFirstArticle(batchId: string, inspectionId: string, signal: AbortSignal): Promise<FirstArticleApproval> {
  return requestJson(`/api/batches/${encodeURIComponent(batchId)}/first-article-approval`, {
    method: 'PUT', signal, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ inspectionId }),
  })
}
export type { PublishedRecipeSummary }
