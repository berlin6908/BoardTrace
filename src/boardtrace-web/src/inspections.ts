export type ExecutionStatus = 'Started' | 'Completed' | 'Failed' | 'Interrupted'
export type Decision = 'NotEvaluated' | 'Pass' | 'Fail'
export type InspectionPurpose = 'EngineeringReplay' | 'FirstArticle' | 'Production'

export interface InspectionSummary {
  id: string
  stationId: string
  productId: string
  sampleId: string
  sourceKind: string
  recipeId: string
  purpose: InspectionPurpose
  batchId: string | null
  productionSequence: number | null
  startedAt: string
  completedAt: string | null
  executionStatus: ExecutionStatus
  decision: Decision
  defectCount: number
  detectionMs: number | null
  receivedAt: string
}

export interface DefectBox {
  box: [number, number, number, number]
  classId: number | null
  score: number
  area: number
}

export interface InspectionRecord extends Omit<InspectionSummary, 'defectCount' | 'receivedAt'> {
  operatorId: string
  operatorName: string
  executionSessionId: string | null
  controllerSessionId: string | null
  triggerSequence: number | null
  recipeJson: string
  width: number
  height: number
  defects: DefectBox[]
  diagnostics: Record<string, number>
  error: string | null
  testedImage: null
  referenceImage: null
}

export interface InspectionDetail {
  inspection: InspectionRecord
  receivedAt: string
  contentHash: string
  hasTestedImage: boolean
  hasReferenceImage: boolean
}

export interface InspectionPage {
  items: InspectionSummary[]
  total: number
  page: number
  pageSize: number
}

export interface InspectionQuery {
  stationId: string
  productId: string
  decision: Decision | ''
  page: number
  pageSize: number
}

export const decisionLabels: Record<Decision, string> = {
  Pass: '通过', Fail: '缺陷', NotEvaluated: '未判定',
}

export const executionLabels: Record<ExecutionStatus, string> = {
  Started: '检测中', Completed: '已完成', Failed: '执行失败', Interrupted: '已中断',
}

export const purposeLabels: Record<InspectionPurpose, string> = {
  EngineeringReplay: '工程回放', FirstArticle: '首件', Production: '生产',
}

export function sourceLabel(kind: string): string {
  if (kind === 'Replay') return '数据集回放 · 模拟'
  if (kind === 'ConstructedNormal') return '构造正常 · 模拟'
  return kind
}

export function classLabel(classId: number | null): string {
  if (classId === null) return '未分类区域'
  return ({ 1: '断路', 2: '短路', 3: '缺口', 4: '毛刺', 5: '余铜', 6: '针孔' } as Record<number, string>)[classId] ?? `类别 ${classId}`
}

const timestampFormat = new Intl.DateTimeFormat('zh-CN', {
  year: 'numeric', month: '2-digit', day: '2-digit',
  hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
})

export function formatTime(value: string | null): string {
  return value ? timestampFormat.format(new Date(value)) : '—'
}

export function formatMs(value: number | null): string {
  return value === null ? '—' : `${value.toFixed(1)} ms`
}
