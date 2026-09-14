<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElAlert, ElButton, ElDialog, ElDrawer, ElEmpty, ElForm, ElFormItem, ElInput, ElInputNumber, ElOption, ElSelect, ElSkeleton, ElTable, ElTableColumn, ElTag } from 'element-plus'
import { ApiError, requestError } from './api'
import type { CurrentUser } from './api'
import { approveFirstArticle, batchStatusLabels, createBatch, getBatch, listBatches, listStations } from './batches'
import type { BatchDetails, BatchSummary, CreateBatchRequest, StationSummary } from './batches'
import { getInspection } from './api'
import { decisionLabels, executionLabels, formatTime, sourceLabel } from './inspections'
import type { InspectionDetail } from './inspections'
import { listPublishedRecipes } from './recipes'
import type { PublishedRecipeSummary } from './recipes'
import InspectionDetailView from './components/InspectionDetail.vue'
import './batches.css'

const props = defineProps<{ user: CurrentUser }>()
const emit = defineEmits<{ 'session-expired': [] }>()
const canCreate = computed(() => props.user.roles.includes('ProcessEngineer'))
const canApprove = computed(() => props.user.roles.includes('QualityEngineer'))
const batches = ref<BatchSummary[]>([])
const loading = ref(true)
const listError = ref('')
const selectedId = ref('')
const detail = ref<BatchDetails | null>(null)
const detailLoading = ref(false)
const detailError = ref('')
const createOpen = ref(false)
const creating = ref(false)
const createError = ref('')
const stations = ref<StationSummary[]>([])
const versions = ref<PublishedRecipeSummary[]>([])
const choicesLoading = ref(false)
const choicesError = ref('')
const inspectionId = ref('')
const inspected = ref<InspectionDetail | null>(null)
const inspectionLoading = ref(false)
const inspectionError = ref('')
const evidenceReadyId = ref('')
const approving = ref(false)
const approvalError = ref('')
const form = reactive<CreateBatchRequest>({ batchNumber: '', productType: '', fieldOfView: '', plannedQuantity: 1, stationId: '', recipeVersionId: '' })
const lifetime = new AbortController()
let listRequest: AbortController | undefined
let detailRequest: AbortController | undefined
let choicesRequest: AbortController | undefined
let inspectionRequest: AbortController | undefined

const selectedBatch = computed(() => detail.value?.batch)
const selectedCandidate = computed(() => detail.value?.firstArticles.find(item => item.id === inspectionId.value))
const canSubmit = computed(() => canCreate.value && !creating.value && !choicesLoading.value && !choicesError.value && versions.value.length > 0
  && !!form.batchNumber.trim() && !!form.productType.trim() && !!form.fieldOfView.trim()
  && Number.isInteger(form.plannedQuantity) && form.plannedQuantity > 0 && !!form.stationId && !!form.recipeVersionId)
const canApproveCandidate = computed(() => canApprove.value && detail.value?.status === 'AwaitingFirstArticle' && !detail.value.approval
  && selectedCandidate.value?.executionStatus === 'Completed' && selectedCandidate.value.decision === 'Pass'
  && inspected.value?.inspection.id === inspectionId.value && inspected.value.inspection.purpose === 'FirstArticle'
  && inspected.value.inspection.batchId === detail.value.batch.id && inspected.value.hasTestedImage && inspected.value.hasReferenceImage
  && evidenceReadyId.value === inspectionId.value)

function showError(cause: unknown, target: typeof listError) {
  if (cause instanceof ApiError && cause.status === 401) emit('session-expired')
  else target.value = requestError(cause)
}

async function load() {
  listRequest?.abort()
  const request = new AbortController()
  listRequest = request
  loading.value = true
  listError.value = ''
  try {
    const result = await listBatches(request.signal)
    if (!request.signal.aborted) batches.value = result
  } catch (cause) { if (!request.signal.aborted) showError(cause, listError) }
  finally { if (!request.signal.aborted) loading.value = false }
}

async function selectBatch(id: string) {
  detailRequest?.abort()
  inspectionRequest?.abort()
  selectedId.value = id
  detail.value = null
  inspectionId.value = ''
  inspected.value = null
  evidenceReadyId.value = ''
  detailError.value = ''
  approvalError.value = ''
  if (!id) return
  const request = new AbortController()
  detailRequest = request
  detailLoading.value = true
  try {
    const result = await getBatch(id, request.signal)
    if (!request.signal.aborted && selectedId.value === id) detail.value = result
  } catch (cause) { if (!request.signal.aborted) showError(cause, detailError) }
  finally { if (!request.signal.aborted) detailLoading.value = false }
}

async function loadChoices() {
  choicesRequest?.abort()
  const request = new AbortController()
  choicesRequest = request
  choicesLoading.value = true
  choicesError.value = ''
  try {
    const [nextStations, nextVersions] = await Promise.all([listStations(request.signal), listPublishedRecipes(request.signal)])
    if (!request.signal.aborted) {
      stations.value = nextStations
      versions.value = nextVersions
      if (!nextStations.some(item => item.stationId === form.stationId)) form.stationId = nextStations[0]?.stationId ?? ''
      if (!nextVersions.some(item => item.id === form.recipeVersionId)) form.recipeVersionId = nextVersions[0]?.id ?? ''
    }
  } catch (cause) { if (!request.signal.aborted) showError(cause, choicesError) }
  finally { if (!request.signal.aborted) choicesLoading.value = false }
}

function openCreate() { createError.value = ''; createOpen.value = true; void loadChoices() }
async function submitCreate() {
  if (!canSubmit.value) return
  creating.value = true
  createError.value = ''
  try {
    const created = await createBatch({ ...form, batchNumber: form.batchNumber.trim(), productType: form.productType.trim(), fieldOfView: form.fieldOfView.trim() }, lifetime.signal)
    if (lifetime.signal.aborted) return
    createOpen.value = false
    await load()
    await selectBatch(created.batch.id)
  } catch (cause) {
    if (!lifetime.signal.aborted) {
      showError(cause, createError)
      if (!(cause instanceof ApiError)) createError.value += ' 请求可能已提交，请刷新批次列表核对批号。'
    }
  } finally { creating.value = false }
}

async function inspect(id: string) {
  inspectionRequest?.abort()
  inspectionId.value = id
  inspected.value = null
  evidenceReadyId.value = ''
  inspectionError.value = ''
  approvalError.value = ''
  const request = new AbortController()
  inspectionRequest = request
  inspectionLoading.value = true
  try {
    const result = await getInspection(id, request.signal)
    if (!request.signal.aborted && inspectionId.value === id) inspected.value = result
  } catch (cause) { if (!request.signal.aborted) showError(cause, inspectionError) }
  finally { if (!request.signal.aborted) inspectionLoading.value = false }
}

async function approve() {
  const batchId = detail.value?.batch.id
  const candidateId = inspectionId.value
  if (!batchId || !canApproveCandidate.value || approving.value) return
  approving.value = true
  approvalError.value = ''
  try {
    await approveFirstArticle(batchId, candidateId, lifetime.signal)
    if (lifetime.signal.aborted) return
    inspectionId.value = ''
    inspected.value = null
    await selectBatch(batchId)
    await load()
  } catch (cause) {
    if (!lifetime.signal.aborted) {
      showError(cause, approvalError)
      if (!(cause instanceof ApiError)) approvalError.value += ' 请求可能已提交，请刷新批次详情确认批准记录。'
    }
  } finally { approving.value = false }
}

function toggleBatchDrawer(open: boolean) { if (!open) void selectBatch('') }
function toggleInspectionDrawer(open: boolean) {
  if (!open) { inspectionRequest?.abort(); inspectionId.value = ''; inspected.value = null; evidenceReadyId.value = '' }
}

onMounted(load)
onBeforeUnmount(() => { lifetime.abort(); listRequest?.abort(); detailRequest?.abort(); choicesRequest?.abort(); inspectionRequest?.abort() })
</script>

<template>
  <main class="workspace batches-workspace">
    <div class="page-heading"><div><p class="eyebrow">工艺与质量 · 生产批次</p><h1>批次与首件</h1><p class="subtitle">批次绑定已发布方案和工位；首件原始证据由质量工程师审核。</p></div><div class="batch-actions"><ElButton :loading="loading" @click="load">刷新批次</ElButton><ElButton v-if="canCreate" type="primary" @click="openCreate">创建批次</ElButton></div></div>
    <section class="records-panel batch-list-panel">
      <div class="results-heading"><h2>批次列表 <span class="record-count">{{ batches.length }}</span></h2><span class="section-note">生产数量仅统计中央已接收记录，工位可能仍有待上传原件。</span></div>
      <div v-if="listError" class="state-panel"><ElAlert :title="listError" type="error" :closable="false" show-icon /><ElButton @click="load">重新读取</ElButton></div>
      <div v-else-if="loading" class="list-loading"><ElSkeleton :rows="5" animated /></div>
      <ElEmpty v-else-if="!batches.length" description="暂无批次"><p class="empty-help">需要先有达到正式质量目标的已发布方案，工艺工程师才能建立生产批次。</p></ElEmpty>
      <ElTable v-else :data="batches" row-key="batch.id" class="inspection-table">
        <ElTableColumn label="批号 / 产品" min-width="190"><template #default="{ row }"><button class="record-link" @click="selectBatch(row.batch.id)">{{ row.batch.batchNumber }}</button><span class="secondary-line">{{ row.batch.productType }} · {{ row.batch.fieldOfView }}</span></template></ElTableColumn>
        <ElTableColumn label="工位" min-width="120"><template #default="{ row }">{{ row.batch.stationId }}</template></ElTableColumn>
        <ElTableColumn label="状态" min-width="135"><template #default="{ row }"><ElTag>{{ batchStatusLabels[row.status as keyof typeof batchStatusLabels] }}</ElTag></template></ElTableColumn>
        <ElTableColumn label="中央已接收 / 计划" min-width="155"><template #default="{ row }">{{ row.receivedProductionCount }} / {{ row.batch.plannedQuantity }}</template></ElTableColumn>
        <ElTableColumn label="创建时间" min-width="165"><template #default="{ row }">{{ formatTime(row.batch.createdAt) }}</template></ElTableColumn>
        <ElTableColumn label="" width="76"><template #default="{ row }"><ElButton link @click="selectBatch(row.batch.id)">详情</ElButton></template></ElTableColumn>
      </ElTable>
    </section>
    <p class="page-footnote">首件不消耗计划生产数量。构造正常输入仅用于模拟流程，不能作为真实良品质量证据。</p>

    <ElDialog v-model="createOpen" title="创建定站批次" width="min(620px, 94vw)" :close-on-click-modal="!creating" :close-on-press-escape="!creating" :show-close="!creating">
      <p class="section-note">批号、产品/视野、工位与版本创建后固定。仅可选择中央已经正式发布的方案。</p>
      <ElAlert v-if="choicesError" :title="choicesError" type="error" :closable="false" show-icon><ElButton link @click="loadChoices">重试读取版本和工位</ElButton></ElAlert>
      <ElSkeleton v-if="choicesLoading" :rows="3" animated />
      <ElAlert v-else-if="!choicesError && !versions.length" title="当前没有已发布且满足正式质量目标的方案，不能创建批次。请先完成方案验证与发布。" type="warning" :closable="false" show-icon />
      <ElForm v-if="!choicesLoading" label-position="top" class="batch-form" @submit.prevent="submitCreate">
        <ElFormItem label="批号"><ElInput v-model="form.batchNumber" maxlength="80" :disabled="creating" /></ElFormItem>
        <ElFormItem label="产品类型"><ElInput v-model="form.productType" maxlength="120" :disabled="creating" /></ElFormItem>
        <ElFormItem label="视野定义"><ElInput v-model="form.fieldOfView" maxlength="120" :disabled="creating" /></ElFormItem>
        <ElFormItem label="计划数量"><ElInputNumber v-model="form.plannedQuantity" :min="1" :max="1000000" :precision="0" :disabled="creating" controls-position="right" /></ElFormItem>
        <ElFormItem label="指定工位"><ElSelect v-model="form.stationId" :disabled="creating || !!choicesError"><ElOption v-for="station in stations" :key="station.stationId" :label="`${station.displayName} · ${station.stationId}`" :value="station.stationId" /></ElSelect></ElFormItem>
        <ElFormItem label="已发布方案"><ElSelect v-model="form.recipeVersionId" :disabled="creating || !!choicesError || !versions.length"><ElOption v-for="version in versions" :key="version.id" :label="`${version.name} · ${version.id.slice(0, 8)}`" :value="version.id" /></ElSelect></ElFormItem>
        <ElAlert v-if="createError" :title="createError" type="error" :closable="false" show-icon><ElButton link @click="load">刷新批次列表</ElButton></ElAlert>
        <div class="batch-actions"><ElButton :disabled="creating" @click="createOpen = false">取消</ElButton><ElButton type="primary" native-type="submit" :disabled="!canSubmit" :loading="creating">创建批次</ElButton></div>
      </ElForm>
    </ElDialog>

    <ElDrawer :model-value="!!selectedId" title="批次详情" size="min(900px, 96vw)" @update:model-value="toggleBatchDrawer">
      <div class="batch-detail">
        <ElSkeleton v-if="detailLoading" :rows="8" animated />
        <div v-else-if="detailError" class="state-panel"><ElAlert :title="detailError" type="error" :closable="false" show-icon /><ElButton @click="selectBatch(selectedId)">重试详情</ElButton></div>
        <template v-else-if="detail && selectedBatch">
          <div class="section-heading"><h2>{{ selectedBatch.batchNumber }}</h2><ElButton @click="selectBatch(selectedId)">刷新详情</ElButton></div>
          <ElTag>{{ batchStatusLabels[detail.status] }}</ElTag>
          <dl class="detail-metadata batch-metadata"><div><dt>产品类型</dt><dd>{{ selectedBatch.productType }}</dd></div><div><dt>视野</dt><dd>{{ selectedBatch.fieldOfView }}</dd></div><div><dt>指定工位</dt><dd>{{ selectedBatch.stationId }}</dd></div><div><dt>方案版本</dt><dd>{{ selectedBatch.recipeVersionId }}</dd></div><div><dt>计划数量</dt><dd>{{ selectedBatch.plannedQuantity }}</dd></div><div><dt>中央已接收生产</dt><dd>{{ detail.receivedProductionCount }}</dd></div><div><dt>技术失败</dt><dd>{{ detail.technicalFailureCount }}</dd></div><div><dt>创建人 / 时间</dt><dd>{{ selectedBatch.createdByName }} · {{ formatTime(selectedBatch.createdAt) }}</dd></div></dl>
          <p class="section-note">中央已接收数量不包含工位尚未上传的记录。</p>
          <section class="batch-first-article"><h3>首件审核</h3><ElAlert v-if="detail.approval" :title="`已由 ${detail.approval.approvedByName} 于 ${formatTime(detail.approval.approvedAt)} 批准 · 检测 ${detail.approval.inspectionId}`" type="success" :closable="false" show-icon /><p v-else class="section-note">等待有效首件检测同步中央后，由质量工程师查看图像与机器原判并批准。</p>
            <ElEmpty v-if="!detail.firstArticles.length" description="中央尚未收到此批次首件" />
            <div v-else class="batch-candidates"><button v-for="candidate in detail.firstArticles" :key="candidate.id" type="button" class="batch-candidate" @click="inspect(candidate.id)"><strong>{{ candidate.productId }}</strong><span>{{ decisionLabels[candidate.decision] }} · {{ executionLabels[candidate.executionStatus] }} · {{ sourceLabel(candidate.sourceKind) }}</span><small>{{ candidate.operatorName }} · {{ formatTime(candidate.startedAt) }}</small></button></div>
          </section>
        </template>
      </div>
    </ElDrawer>

    <ElDrawer :model-value="!!inspectionId" title="首件图像与原判" size="min(1220px, 96vw)" class="inspection-drawer" @update:model-value="toggleInspectionDrawer">
      <div v-if="inspectionLoading" class="detail-loading"><ElSkeleton :rows="7" animated /></div>
      <div v-else-if="inspectionError" class="state-panel"><ElAlert :title="inspectionError" type="error" :closable="false" show-icon /><ElButton @click="inspect(inspectionId)">重试首件</ElButton></div>
      <template v-else-if="inspected">
        <InspectionDetailView :id="inspectionId" @evidence-ready="evidenceReadyId = $event" @session-expired="emit('session-expired')" />
        <div v-if="canApprove" class="batch-approval-actions"><ElAlert v-if="approvalError" :title="approvalError" type="error" :closable="false" show-icon><ElButton link @click="selectBatch(selectedId)">刷新批次详情</ElButton></ElAlert><p class="section-note">请核对上方原图、参考图、输入来源和机器原判；构造正常输入仍标为模拟。</p><ElButton type="primary" :loading="approving" :disabled="!canApproveCandidate || approving" @click="approve">批准此首件</ElButton><span v-if="!canApproveCandidate" class="section-note">仅本批次、双图已加载且机器执行完成并判定通过的首件可批准。</span></div>
      </template>
    </ElDrawer>
  </main>
</template>
