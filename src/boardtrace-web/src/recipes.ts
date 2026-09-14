import { ApiError, requestJson } from './api'
import type { Decision, DefectBox } from './inspections'

export interface ClassicalSettings {
  binarizationThreshold: number
  edgeTolerance: number
  minimumArea: number
  closingSize: number
  boxPadding: number
  maximumTranslation: number
  minimumAlignmentResponse: number
}

export interface RecipeTargets { minPrecision: number; minRecall: number; maxP95Ms: number }
export interface ReleasePolicy { isFrozen: boolean; targets: RecipeTargets | null; reason: string | null }
export interface RecipeScoreThresholds { open: number; short: number; mousebite: number; spur: number; copper: number; pinHole: number }
export type RecipeDefinition = { algorithm: 'Classical'; settings: ClassicalSettings } | { algorithm: 'PairedOnnx'; modelSha256: string; thresholds: RecipeScoreThresholds }
export interface RecipeModelSummary { sha256: string; byteLength: number; inputContract: string; createdAt: string }
export const pairedInputContract = 'PairedGrayAbsDiff640V1'
export interface SaveRecipeDraft { name: string; definition: RecipeDefinition; targets: RecipeTargets }
export interface RecipeSnapshot extends SaveRecipeDraft { dataManifestSha256: string; snapshotHash: string }
export interface RecipeDraft extends RecipeSnapshot { id: string; updatedAt: string }
export type ValidationStatus = 'Queued' | 'Running' | 'Completed' | 'Failed'
export interface ValidationRow {
  sampleId: string
  status: 'Completed' | 'Failed'
  decision: Decision
  defects: DefectBox[]
  error: string | null
  elapsedMs: number | null
  tp: number
  fp: number
  fn: number
}
export interface ValidationReport {
  tp: number
  fp: number
  fn: number
  precision: number
  recall: number
  f1: number
  coldSampleMs: number | null
  p50Ms: number
  p95Ms: number
  executionFailures: number
  meetsTargets: boolean
  rows: ValidationRow[]
  algorithmAssemblySha256: string
  runtime: string
  machine: string
  timingDescription: string
  matchingMode: 'Localization' | 'ClassAware'
  classes: { classId: number; tp: number; fp: number; fn: number; precision: number; recall: number; f1: number }[]
  sessionInitializationMs: number | null
  modelSha256: string | null
}
export interface ValidationRun {
  id: string
  draftId: string
  status: ValidationStatus
  processed: number
  total: number
  snapshot: RecipeSnapshot
  report: ValidationReport | null
  error: string | null
  createdAt: string
  completedAt: string | null
}

export interface PublishedRecipeSummary {
  id: string
  draftId: string
  validationRunId: string
  name: string
  algorithm: 'Classical' | 'PairedOnnx'
  bundleHash: string
  publishedById: string
  publishedByName: string
  publishedAt: string
}
export interface PublishedRecipeBundle {
  versionId: string
  draftId: string
  validationRunId: string
  name: string
  definition: RecipeDefinition
  targets: RecipeTargets
  releaseTargets: RecipeTargets
  input: { width: number; height: number; requiresReference: boolean }
  algorithmAssemblySha256: string
  inputManifestSha256: string
  validationSnapshotHash: string
  references: { sampleId: string; assetId: string; sha256: string; byteLength: number }[]
  model: { assetId: string; sha256: string; byteLength: number; inputContract: string } | null
  publishedById: string
  publishedByName: string
  publishedAt: string
}
export interface PublishedRecipeVersion { bundle: PublishedRecipeBundle; bundleHash: string }

export const defaultClassicalSettings: ClassicalSettings = {
  binarizationThreshold: 127, edgeTolerance: 1, minimumArea: 8, closingSize: 3,
  boxPadding: 10, maximumTranslation: 12, minimumAlignmentResponse: 0.1,
}
export const settingLabels: Record<keyof ClassicalSettings, string> = {
  binarizationThreshold: '二值化阈值', edgeTolerance: '边缘容差（像素）', minimumArea: '最小区域面积（像素）',
  closingSize: '闭运算核尺寸（奇数）', boxPadding: '框外扩（像素）', maximumTranslation: '最大平移（像素）',
  minimumAlignmentResponse: '最低配准响应',
}
export const thresholdLabels: Record<keyof RecipeScoreThresholds, string> = {
  open: '断路 Open', short: '短路 Short', mousebite: '缺口 Mousebite', spur: '毛刺 Spur', copper: '余铜 Copper', pinHole: '针孔 PinHole',
}
export const algorithmLabels = { Classical: '经典定位', PairedOnnx: '成对 ONNX 六类检测' } as const
export const validationLabels: Record<ValidationStatus, string> = {
  Queued: '排队中', Running: '验证中', Completed: '已完成', Failed: '验证失败',
}
export function isActiveRun(run: ValidationRun): boolean { return run.status === 'Queued' || run.status === 'Running' }
export function formatPercent(value: number): string { return `${(value * 100).toFixed(2)}%` }

const draftUrl = (id: string) => `/api/recipes/drafts/${encodeURIComponent(id)}`
export function listDrafts(signal: AbortSignal): Promise<RecipeDraft[]> {
  return requestJson('/api/recipes/drafts', { signal })
}
export function saveDraft(id: string | undefined, draft: SaveRecipeDraft, signal: AbortSignal): Promise<RecipeDraft> {
  return requestJson(id ? draftUrl(id) : '/api/recipes/drafts', {
    method: id ? 'PUT' : 'POST', signal, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(draft),
  })
}
export function listValidations(draftId: string, signal: AbortSignal): Promise<ValidationRun[]> {
  return requestJson(`${draftUrl(draftId)}/validations`, { signal })
}
export function startValidation(draftId: string, signal: AbortSignal): Promise<ValidationRun> {
  return requestJson(`${draftUrl(draftId)}/validations`, { method: 'POST', signal })
}
export function getValidation(id: string, signal: AbortSignal): Promise<ValidationRun> {
  return requestJson(`/api/recipes/validations/${encodeURIComponent(id)}`, { signal })
}
export function publishRecipe(draftId: string, validationRunId: string, signal: AbortSignal): Promise<PublishedRecipeVersion> {
  return requestJson(`${draftUrl(draftId)}/publish`, {
    method: 'POST', signal, headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ validationRunId }),
  })
}
export function listPublishedRecipes(signal: AbortSignal): Promise<PublishedRecipeSummary[]> {
  return requestJson('/api/recipes/versions', { signal })
}
export function getPublishedRecipe(id: string, signal: AbortSignal): Promise<PublishedRecipeVersion> {
  return requestJson(`/api/recipes/versions/${encodeURIComponent(id)}`, { signal })
}
export function getReleasePolicy(signal: AbortSignal): Promise<ReleasePolicy> {
  return requestJson('/api/recipes/release-policy', { signal })
}
export function listRecipeModels(signal: AbortSignal): Promise<RecipeModelSummary[]> { return requestJson('/api/recipes/models', { signal }) }
export async function uploadRecipeModel(file: File, sha256: string, signal: AbortSignal): Promise<RecipeModelSummary> {
  const url = `/api/recipes/models?${new URLSearchParams({ sha256, inputContract: pairedInputContract })}`
  const response = await fetch(url, { method: 'POST', credentials: 'same-origin', headers: { Accept: 'application/json', 'Content-Type': 'application/octet-stream' }, body: file, signal })
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { detail?: string; title?: string } | null
    throw new ApiError(response.status, problem?.detail || problem?.title || `模型上传失败（${response.status}）`)
  }
  return response.json() as Promise<RecipeModelSummary>
}
