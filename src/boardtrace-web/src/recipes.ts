import { requestJson } from './api'
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
export interface SaveRecipeDraft { name: string; settings: ClassicalSettings; targets: RecipeTargets }
export interface RecipeSnapshot extends SaveRecipeDraft { dataManifestSha256: string; snapshotHash: string }
export interface RecipeDraft extends RecipeSnapshot { id: string; algorithm: 'Classical'; updatedAt: string }
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
  algorithm: 'Classical'
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
  algorithm: 'Classical'
  settings: ClassicalSettings
  targets: RecipeTargets
  releaseTargets: RecipeTargets
  input: { width: number; height: number; requiresReference: boolean }
  algorithmAssemblySha256: string
  inputManifestSha256: string
  validationSnapshotHash: string
  references: { sampleId: string; assetId: string; sha256: string; byteLength: number }[]
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
