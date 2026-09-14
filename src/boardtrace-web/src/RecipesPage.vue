<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElAlert, ElButton, ElDialog, ElDrawer, ElEmpty, ElInput, ElOption, ElProgress, ElSelect, ElSkeleton, ElTag } from 'element-plus'
import { ApiError, requestError } from './api'
import type { CurrentUser } from './api'
import { formatMs, formatTime } from './inspections'
import { algorithmLabels, formatPercent, getPublishedRecipe, getReleasePolicy, getValidation, isActiveRun, listDrafts, listPublishedRecipes, listRecipeModels, listValidations, pairedInputContract, publishRecipe, saveDraft, settingLabels, startValidation, thresholdLabels, uploadRecipeModel, validationLabels } from './recipes'
import type { PublishedRecipeSummary, PublishedRecipeVersion, RecipeDraft, RecipeModelSummary, ReleasePolicy, SaveRecipeDraft, ValidationRun } from './recipes'
import DraftEditor from './components/DraftEditor.vue'
import RecipeReport from './components/RecipeReport.vue'
import './recipes.css'

const props = defineProps<{ user: CurrentUser }>()
const emit = defineEmits<{ 'session-expired': [] }>()
const canEdit = computed(() => props.user.roles.includes('ProcessEngineer'))
const drafts = ref<RecipeDraft[]>([])
const selectedId = ref('')
const selectedDraft = computed(() => drafts.value.find(draft => draft.id === selectedId.value) ?? null)
const loading = ref(true)
const listError = ref('')
const historyLoading = ref(false)
const historyError = ref('')
const runs = ref<ValidationRun[]>([])
const selectedRun = ref<ValidationRun | null>(null)
const runError = ref('')
const tracking = ref(false)
const starting = ref(false)
const editorOpen = ref(false)
const editingDraft = ref<RecipeDraft | null>(null)
const editorKey = ref(0)
const saving = ref(false)
const saveError = ref('')
const publishing = ref(false)
const publishError = ref('')
const releasePolicy = ref<ReleasePolicy | null>(null)
const policyLoading = ref(true)
const policyError = ref('')
const models = ref<RecipeModelSummary[]>([])
const modelsLoading = ref(false)
const modelsError = ref('')
const modelFile = ref<File | null>(null)
const expectedModelSha = ref('')
const uploadingModel = ref(false)
const uploadError = ref('')
const uploadNotice = ref('')
const versionsOpen = ref(false)
const versions = ref<PublishedRecipeSummary[]>([])
const versionsLoading = ref(false)
const versionsError = ref('')
const selectedVersionId = ref('')
const selectedVersion = ref<PublishedRecipeVersion | null>(null)
const versionLoading = ref(false)
const versionError = ref('')
const lifetime = new AbortController()
let listRequest: AbortController | undefined
let historyRequest: AbortController | undefined
let runRequest: AbortController | undefined
let versionsRequest: AbortController | undefined
let versionRequest: AbortController | undefined
let policyRequest: AbortController | undefined
let modelsRequest: AbortController | undefined
let pollTimer: ReturnType<typeof setTimeout> | undefined
const busy = computed(() => saving.value || starting.value || publishing.value || uploadingModel.value)
const currentRunActive = computed(() => runs.value.some(run => isActiveRun(run) && run.snapshot.snapshotHash === selectedDraft.value?.snapshotHash))
const selectedPublication = computed(() => versions.value.find(version => version.validationRunId === selectedRun.value?.id))
const meetsReleasePolicy = computed(() => {
  const targets = releasePolicy.value?.targets
  const report = selectedRun.value?.report
  return releasePolicy.value?.isFrozen === true && !!targets && !!report
    && report.precision >= targets.minPrecision && report.recall >= targets.minRecall && report.p95Ms <= targets.maxP95Ms
})
const canPublish = computed(() => canEdit.value && selectedDraft.value && selectedRun.value?.status === 'Completed'
  && selectedRun.value.report?.meetsTargets === true && selectedRun.value.report.executionFailures === 0
  && selectedRun.value.snapshot.snapshotHash === selectedDraft.value.snapshotHash && !selectedPublication.value
  && meetsReleasePolicy.value && !policyError.value && !policyLoading.value)

function showError(cause: unknown, target: typeof listError) {
  if (cause instanceof ApiError && cause.status === 401) emit('session-expired')
  else target.value = requestError(cause)
}

function stopTracking() {
  tracking.value = false
  clearTimeout(pollTimer)
  runRequest?.abort()
}

async function refreshRun(id: string) {
  const request = new AbortController()
  runRequest = request
  try {
    const run = await getValidation(id, request.signal)
    if (request.signal.aborted) return
    selectedRun.value = run
    runs.value = runs.value.map(previous => previous.id === run.id ? run : previous)
    runError.value = ''
    if (isActiveRun(run) && tracking.value) pollTimer = setTimeout(() => { void refreshRun(id) }, 1000)
    else tracking.value = false
  } catch (cause) {
    if (!request.signal.aborted) { tracking.value = false; showError(cause, runError) }
  }
}

function selectRun(id: string) {
  stopTracking()
  selectedRun.value = runs.value.find(run => run.id === id) ?? null
  runError.value = ''
  if (selectedRun.value) {
    tracking.value = isActiveRun(selectedRun.value)
    void refreshRun(id)
  }
}

async function loadRuns(draftId: string) {
  historyRequest?.abort()
  stopTracking()
  const request = new AbortController()
  historyRequest = request
  const previousId = selectedRun.value?.draftId === draftId ? selectedRun.value.id : ''
  runs.value = []
  selectedRun.value = null
  historyLoading.value = true
  historyError.value = ''
  runError.value = ''
  try {
    const response = await listValidations(draftId, request.signal)
    if (request.signal.aborted) return
    runs.value = response
    const next = response.find(run => run.id === previousId) ?? response[0]
    if (next) selectRun(next.id)
  } catch (cause) {
    if (!request.signal.aborted) showError(cause, historyError)
  } finally {
    if (!request.signal.aborted) historyLoading.value = false
  }
}

function selectDraft(id: string) {
  selectedId.value = id
  void loadRuns(id)
}

async function loadDrafts() {
  listRequest?.abort()
  const request = new AbortController()
  listRequest = request
  loading.value = true
  listError.value = ''
  try {
    const response = await listDrafts(request.signal)
    if (request.signal.aborted) return
    drafts.value = response
    const next = response.find(draft => draft.id === selectedId.value) ?? response[0]
    if (next) selectDraft(next.id)
    else { selectedId.value = ''; historyRequest?.abort(); stopTracking(); runs.value = []; selectedRun.value = null }
  } catch (cause) {
    if (!request.signal.aborted) showError(cause, listError)
  } finally {
    if (!request.signal.aborted) loading.value = false
  }
}

function editDraft(draft: RecipeDraft | null) {
  editingDraft.value = draft
  saveError.value = ''
  editorKey.value++
  editorOpen.value = true
}

async function save(input: SaveRecipeDraft) {
  if (!canEdit.value || saving.value) return
  saving.value = true
  saveError.value = ''
  try {
    const draft = await saveDraft(editingDraft.value?.id, input, lifetime.signal)
    if (lifetime.signal.aborted) return
    drafts.value = [draft, ...drafts.value.filter(previous => previous.id !== draft.id)]
    editorOpen.value = false
    selectDraft(draft.id)
  } catch (cause) {
    if (!lifetime.signal.aborted) showError(cause, saveError)
  } finally { saving.value = false }
}

async function validateDraft() {
  if (!canEdit.value || !selectedDraft.value || busy.value) return
  starting.value = true
  runError.value = ''
  try {
    const run = await startValidation(selectedDraft.value.id, lifetime.signal)
    if (lifetime.signal.aborted) return
    runs.value = [run, ...runs.value]
    selectRun(run.id)
  } catch (cause) {
    if (!lifetime.signal.aborted) {
      showError(cause, runError)
      if (!(cause instanceof ApiError)) runError.value += ' 请求可能已被接收，请先刷新验证记录确认。'
    }
  } finally { starting.value = false }
}

async function selectVersion(id: string) {
  versionRequest?.abort()
  selectedVersionId.value = id
  selectedVersion.value = null
  versionError.value = ''
  if (!id) return
  const request = new AbortController()
  versionRequest = request
  versionLoading.value = true
  try {
    const version = await getPublishedRecipe(id, request.signal)
    if (!request.signal.aborted && selectedVersionId.value === id) selectedVersion.value = version
  } catch (cause) {
    if (!request.signal.aborted) showError(cause, versionError)
  } finally { if (!request.signal.aborted) versionLoading.value = false }
}

async function loadVersions(focusId = selectedVersionId.value) {
  versionsRequest?.abort()
  const request = new AbortController()
  versionsRequest = request
  versionsLoading.value = true
  versionsError.value = ''
  try {
    const response = await listPublishedRecipes(request.signal)
    if (request.signal.aborted) return
    versions.value = response
    if (versionsOpen.value) {
      const next = response.find(version => version.id === focusId) ?? response[0]
      await selectVersion(next?.id ?? '')
    }
  } catch (cause) {
    if (!request.signal.aborted) showError(cause, versionsError)
  } finally { if (!request.signal.aborted) versionsLoading.value = false }
}

async function loadReleasePolicy() {
  policyRequest?.abort()
  const request = new AbortController()
  policyRequest = request
  policyLoading.value = true
  policyError.value = ''
  releasePolicy.value = null
  try {
    const response = await getReleasePolicy(request.signal)
    if (!request.signal.aborted) releasePolicy.value = response
  } catch (cause) {
    if (!request.signal.aborted) showError(cause, policyError)
  } finally { if (!request.signal.aborted) policyLoading.value = false }
}

async function loadModels() {
  modelsRequest?.abort()
  const request = new AbortController()
  modelsRequest = request
  modelsLoading.value = true
  modelsError.value = ''
  try { const response = await listRecipeModels(request.signal); if (!request.signal.aborted) models.value = response }
  catch (cause) { if (!request.signal.aborted) showError(cause, modelsError) }
  finally { if (!request.signal.aborted) modelsLoading.value = false }
}

function chooseModelFile(event: Event) {
  modelFile.value = (event.target as HTMLInputElement).files?.[0] ?? null
  uploadError.value = ''
  uploadNotice.value = ''
}

async function uploadModel() {
  const file = modelFile.value
  const sha = expectedModelSha.value.trim().toLowerCase()
  if (!canEdit.value || uploadingModel.value || !file) return
  if (!/^[0-9a-f]{64}$/.test(sha) || file.size < 1 || file.size > 200 * 1024 * 1024) {
    uploadError.value = '请选不超过 200 MiB 的 ONNX 文件并填写导出清单的完整 64 位 SHA256。'
    return
  }
  uploadingModel.value = true
  uploadError.value = ''
  uploadNotice.value = ''
  try {
    const result = await uploadRecipeModel(file, sha, lifetime.signal)
    if (lifetime.signal.aborted) return
    uploadNotice.value = `模型已入库：${result.sha256} · ${(result.byteLength / 1024 / 1024).toFixed(1)} MiB。`
    modelFile.value = null
    expectedModelSha.value = ''
    await loadModels()
  } catch (cause) {
    if (!lifetime.signal.aborted) {
      showError(cause, uploadError)
      if (!(cause instanceof ApiError)) uploadError.value += ' 请求回执不确定，请先刷新模型列表核对 SHA256。'
    }
  } finally { uploadingModel.value = false }
}

function openVersions(id = '') {
  versionsOpen.value = true
  void loadVersions(id)
}

function closeVersions() {
  versionsRequest?.abort()
  versionRequest?.abort()
  versionsLoading.value = false
  versionLoading.value = false
}

async function publishSelected() {
  const draft = selectedDraft.value
  const run = selectedRun.value
  if (!draft || !run || !canPublish.value || busy.value) return
  publishing.value = true
  publishError.value = ''
  try {
    // The server rereads the release policy during publication. Keep the page's gate fresh too.
    await loadReleasePolicy()
    if (!canPublish.value || lifetime.signal.aborted) return
    const version = await publishRecipe(draft.id, run.id, lifetime.signal)
    if (lifetime.signal.aborted) return
    versionsOpen.value = true
    selectedVersionId.value = version.bundle.versionId
    selectedVersion.value = version
    await loadVersions(version.bundle.versionId)
  } catch (cause) {
    if (!lifetime.signal.aborted) {
      showError(cause, publishError)
      if (!(cause instanceof ApiError)) publishError.value += ' 请求可能已提交，请刷新已发布版本确认。'
    }
  } finally { publishing.value = false }
}

onMounted(() => { void loadDrafts(); void loadVersions(); void loadReleasePolicy(); void loadModels() })
onBeforeUnmount(() => { lifetime.abort(); listRequest?.abort(); historyRequest?.abort(); versionsRequest?.abort(); versionRequest?.abort(); policyRequest?.abort(); modelsRequest?.abort(); stopTracking() })
</script>

<template>
  <main class="workspace recipes-workspace">
    <div class="page-heading"><div><p class="eyebrow">工艺开发 · 固定验证集</p><h1>方案验证</h1><p class="subtitle">经典定位与成对 ONNX 六类检测分别保存定义，在固定 200 张 validation 图像上真实验证。</p></div><div class="validation-history"><ElButton @click="openVersions()">已发布版本</ElButton><ElButton v-if="canEdit" type="primary" :disabled="busy || loading" @click="editDraft(null)">新建草稿</ElButton></div></div>
    <section class="release-policy-panel records-panel" aria-label="正式发布政策">
      <div class="section-heading"><h2>正式发布门槛</h2><ElButton link :loading="policyLoading" @click="loadReleasePolicy">重新读取</ElButton></div>
      <p v-if="policyLoading" class="section-note">正在读取服务器冻结政策…</p>
      <ElAlert v-else-if="policyError" :title="`发布政策读取失败：${policyError}`" type="error" :closable="false" show-icon role="alert" />
      <ElAlert v-else-if="!releasePolicy?.isFrozen" :title="releasePolicy?.reason || '正式质量目标尚未冻结，当前不可发布。'" type="warning" :closable="false" show-icon />
      <template v-else-if="releasePolicy.targets">
        <p class="section-note">以下目标由服务器独立冻结，发布时需同时达到草稿目标和正式目标。</p>
        <dl class="draft-targets"><div><dt>正式最低精确率</dt><dd>{{ formatPercent(releasePolicy.targets.minPrecision) }}</dd></div><div><dt>正式最低召回率</dt><dd>{{ formatPercent(releasePolicy.targets.minRecall) }}</dd></div><div><dt>正式最高 p95</dt><dd>{{ formatMs(releasePolicy.targets.maxP95Ms) }}</dd></div></dl>
      </template>
    </section>
    <section class="records-panel recipe-models" aria-label="已入库ONNX模型"><div class="section-heading"><h2>成对 ONNX 模型资产</h2><ElButton :loading="modelsLoading" @click="loadModels">刷新列表</ElButton></div>
      <p class="section-note">固定输入合同 {{ pairedInputContract }}：tested 灰度、reference 灰度及绝对差，640×640。入库只确认资产身份与合同，不代表模型质量已批准。</p>
      <ElAlert v-if="modelsError" :title="modelsError" type="error" :closable="false" show-icon />
      <ElSkeleton v-if="modelsLoading && !models.length" :rows="2" animated />
      <ElEmpty v-else-if="!models.length && !modelsError" description="尚无已入库 ONNX 模型" :image-size="50" />
      <div v-for="model in models" :key="model.sha256" class="recipe-model-row"><code>{{ model.sha256 }}</code><span>{{ (model.byteLength / 1024 / 1024).toFixed(1) }} MiB · {{ model.inputContract }} · {{ formatTime(model.createdAt) }}</span></div>
      <form v-if="canEdit" class="model-upload" @submit.prevent="uploadModel"><h3>上传正式导出的 ONNX 二进制</h3><p class="section-note">从导出清单复制预期 SHA256；中央会逐字节核对文件和固定输入合同，上传上限 200 MiB。</p><input type="file" accept=".onnx,application/octet-stream" :disabled="uploadingModel" @change="chooseModelFile" aria-label="选择ONNX模型文件" /><label for="expected-model-sha">导出清单 SHA256</label><ElInput id="expected-model-sha" v-model="expectedModelSha" maxlength="64" :disabled="uploadingModel" placeholder="64 位十六进制 SHA256" /><ElAlert v-if="uploadError" :title="uploadError" type="error" :closable="false" show-icon /><ElAlert v-if="uploadNotice" :title="uploadNotice" type="success" :closable="false" show-icon /><ElButton type="primary" native-type="submit" :loading="uploadingModel" :disabled="!modelFile || uploadingModel">入库模型</ElButton></form>
    </section>
    <p v-if="!canEdit" class="recipe-access-note">当前角色可查看方案与验证报告；草稿维护和启动验证由工艺工程师执行。</p>
    <div v-if="loading" class="records-panel list-loading" aria-busy="true"><ElSkeleton :rows="6" animated /></div>
    <div v-else-if="listError" class="records-panel state-panel"><ElAlert :title="listError" type="error" :closable="false" show-icon /><ElButton @click="loadDrafts">重新连接</ElButton></div>
    <div v-else-if="!drafts.length" class="records-panel"><ElEmpty description="尚无方案草稿"><ElButton v-if="canEdit" type="primary" @click="editDraft(null)">创建第一个草稿</ElButton><p v-else class="empty-help">工艺工程师创建草稿后，可在此查看参数与真实验证结果。</p></ElEmpty></div>
    <div v-else class="recipe-layout">
      <aside class="records-panel recipe-list" aria-label="方案草稿列表"><div class="results-heading"><h2>草稿 <span class="record-count">{{ drafts.length }}</span></h2><ElButton link :disabled="busy" @click="loadDrafts">刷新</ElButton></div>
        <button v-for="draft in drafts" :key="draft.id" type="button" class="recipe-choice" :class="{ selected: selectedId === draft.id }" :aria-pressed="selectedId === draft.id" :disabled="busy" @click="selectDraft(draft.id)"><strong>{{ draft.name }}</strong><span>{{ algorithmLabels[draft.definition.algorithm] }}</span><small>{{ formatTime(draft.updatedAt) }}</small></button>
      </aside>
      <div v-if="selectedDraft" class="recipe-content">
        <section class="records-panel recipe-draft">
          <div class="section-heading"><div><h2>{{ selectedDraft.name }}</h2><p class="section-note">草稿 · {{ algorithmLabels[selectedDraft.definition.algorithm] }} · {{ formatTime(selectedDraft.updatedAt) }} 保存</p></div><ElButton v-if="canEdit" :disabled="busy" @click="editDraft(selectedDraft)">编辑草稿</ElButton></div>
          <dl class="draft-targets"><div><dt>最低精确率</dt><dd>{{ formatPercent(selectedDraft.targets.minPrecision) }}</dd></div><div><dt>最低召回率</dt><dd>{{ formatPercent(selectedDraft.targets.minRecall) }}</dd></div><div><dt>最高 p95</dt><dd>{{ formatMs(selectedDraft.targets.maxP95Ms) }}</dd></div></dl>
          <p class="section-note">以上是当前草稿的验收目标，保存时绑定固定验证输入清单；每次验证保留当时的参数和目标。</p>
          <details class="recipe-snapshot"><summary>查看当前参数与输入清单</summary><dl v-if="selectedDraft.definition.algorithm === 'Classical'" class="recipe-settings"><div v-for="(value, key) in selectedDraft.definition.settings" :key="key"><dt>{{ settingLabels[key] }}</dt><dd>{{ value }}</dd></div></dl><template v-else><p class="section-note">模型 SHA256：{{ selectedDraft.definition.modelSha256 }}</p><dl class="recipe-settings"><div v-for="(value, key) in selectedDraft.definition.thresholds" :key="key"><dt>{{ thresholdLabels[key] }}</dt><dd>{{ value }}</dd></div></dl></template><dl class="snapshot-identifiers"><div><dt>草稿快照</dt><dd>{{ selectedDraft.snapshotHash }}</dd></div><div><dt>固定输入清单 SHA256</dt><dd>{{ selectedDraft.dataManifestSha256 }}</dd></div></dl></details>
          <div v-if="canEdit" class="validation-actions"><ElButton type="primary" :loading="starting" :disabled="saving || editorOpen || currentRunActive || historyLoading || Boolean(historyError)" @click="validateDraft">{{ currentRunActive ? '当前草稿正在验证' : '启动验证 · 200 张' }}</ElButton><span class="section-note">使用已保存草稿，后台执行真实图像检测。</span></div>
        </section>
        <section class="records-panel validation-panel">
          <div class="validation-toolbar"><h2>验证记录</h2><div class="validation-history"><label class="sr-only" for="validation-history">选择历史验证</label><ElSelect v-if="runs.length" id="validation-history" :model-value="selectedRun?.id" :disabled="busy" aria-label="选择历史验证" @update:model-value="selectRun"><ElOption v-for="run in runs" :key="run.id" :value="run.id" :label="`${formatTime(run.createdAt)} · ${validationLabels[run.status]} · ${run.snapshot.snapshotHash.slice(0, 8)}`" /></ElSelect><ElButton :disabled="busy || historyLoading" @click="loadRuns(selectedDraft.id)">刷新记录</ElButton></div></div>
          <div v-if="historyLoading" class="list-loading" aria-busy="true"><ElSkeleton :rows="3" animated /></div>
          <ElAlert v-else-if="historyError" :title="historyError" type="error" :closable="false" show-icon />
          <ElAlert v-if="runError" :title="runError" type="error" :closable="false" show-icon role="alert" />
          <template v-if="selectedRun">
            <div class="run-progress" aria-live="polite"><div class="section-heading"><strong>{{ validationLabels[selectedRun.status] }} · {{ selectedRun.processed }} / {{ selectedRun.total }} 张</strong><ElTag v-if="selectedRun.snapshot.snapshotHash === selectedDraft.snapshotHash" type="info">对应当前已保存草稿</ElTag><ElTag v-else type="warning">历史快照</ElTag></div>
              <ElProgress :percentage="Math.round(selectedRun.processed / selectedRun.total * 100)" :status="selectedRun.status === 'Failed' ? 'exception' : undefined" />
              <div class="tracking-actions"><span class="section-note">{{ tracking ? '自动刷新进度。离开页面后后台验证继续。' : isActiveRun(selectedRun) ? '已停止跟踪，后台验证继续。' : `提交于 ${formatTime(selectedRun.createdAt)}` }}</span><ElButton v-if="tracking" link @click="stopTracking">停止跟踪</ElButton><ElButton v-else-if="isActiveRun(selectedRun) || runError" link @click="selectRun(selectedRun.id)">重新获取进度</ElButton></div>
            </div>
            <ElAlert v-if="selectedRun.status === 'Failed'" :title="selectedRun.error || '本次验证执行失败，请检查服务后重新发起。'" type="error" show-icon :closable="false" />
            <RecipeReport :run="selectedRun" :current-snapshot-hash="selectedDraft.snapshotHash" />
            <div v-if="canEdit && selectedRun.status === 'Completed'" class="validation-actions publication-actions">
              <ElButton v-if="selectedPublication" @click="openVersions(selectedPublication.id)">查看已发布版本</ElButton>
              <ElButton v-else type="primary" :loading="publishing" :disabled="!canPublish || busy" @click="publishSelected">发布此验证快照</ElButton>
              <span v-if="!selectedPublication && !releasePolicy?.isFrozen" class="section-note">正式质量目标尚未冻结，当前不可发布；可继续查看报告和改进草稿。</span>
              <span v-else-if="!selectedPublication && !canPublish" class="section-note">仅当前草稿快照、200 张完整且同时达到草稿与正式目标的验证报告可发布。</span>
              <span v-else-if="!selectedPublication" class="section-note">将冻结本次参数、两套目标、验证报告和参考资产；发布后不可修改。</span>
            </div>
            <ElAlert v-if="publishError" :title="publishError" type="error" :closable="false" show-icon role="alert"><ElButton link @click="openVersions()">刷新已发布版本</ElButton></ElAlert>
          </template>
          <ElEmpty v-else-if="!historyLoading && !historyError" description="尚无验证记录"><p class="empty-help">工艺工程师启动验证后，这里显示进度与逐样本报告。</p></ElEmpty>
        </section>
      </div>
    </div>
    <p class="page-footnote">当前页面用于开发方案验证，目标针对各草稿保存。验证输入属于 validation 集合。</p>
    <ElDialog v-model="editorOpen" :title="editingDraft ? '编辑方案草稿' : '新建方案草稿'" width="min(760px, 94vw)" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving" destroy-on-close class="recipe-editor-dialog"><DraftEditor :key="editorKey" :draft="editingDraft" :models="models" :models-loading="modelsLoading" :models-error="modelsError" :saving="saving" :error="saveError" @refresh-models="loadModels" @save="save" @cancel="editorOpen = false" /></ElDialog>
    <ElDrawer v-model="versionsOpen" title="已发布方案版本" size="min(760px, 94vw)" :destroy-on-close="false" @closed="closeVersions">
      <div class="published-drawer">
        <div class="validation-history"><span class="section-note">不可变版本由通过验证的草稿快照发布。</span><ElButton :loading="versionsLoading" @click="loadVersions()">刷新版本</ElButton></div>
        <ElAlert v-if="versionsError" :title="versionsError" type="error" :closable="false" show-icon role="alert" />
        <ElSkeleton v-if="versionsLoading && !versions.length" :rows="4" animated />
        <ElEmpty v-else-if="!versions.length && !versionsError" description="尚无已发布版本" />
        <div v-if="versions.length" class="published-layout">
          <div class="published-list" aria-label="已发布版本列表">
            <button v-for="version in versions" :key="version.id" type="button" class="recipe-choice" :class="{ selected: selectedVersionId === version.id }" :aria-pressed="selectedVersionId === version.id" @click="selectVersion(version.id)">
              <strong>{{ version.name }}</strong><span>{{ version.algorithm }} · {{ formatTime(version.publishedAt) }}</span><small>发布人 {{ version.publishedByName }} · {{ version.id.slice(0, 8) }}</small>
            </button>
          </div>
          <div class="published-detail">
            <ElSkeleton v-if="versionLoading" :rows="7" animated />
            <ElAlert v-else-if="versionError" :title="versionError" type="error" :closable="false" show-icon role="alert"><ElButton link @click="selectVersion(selectedVersionId)">重试详情</ElButton></ElAlert>
            <template v-else-if="selectedVersion">
              <h3>{{ selectedVersion.bundle.name }}</h3>
              <p class="section-note">{{ algorithmLabels[selectedVersion.bundle.definition.algorithm] }} · {{ selectedVersion.bundle.publishedByName }} 于 {{ formatTime(selectedVersion.bundle.publishedAt) }} 发布</p>
              <h4>本草稿验证目标</h4>
              <dl class="draft-targets"><div><dt>最低精确率</dt><dd>{{ formatPercent(selectedVersion.bundle.targets.minPrecision) }}</dd></div><div><dt>最低召回率</dt><dd>{{ formatPercent(selectedVersion.bundle.targets.minRecall) }}</dd></div><div><dt>最高 p95</dt><dd>{{ formatMs(selectedVersion.bundle.targets.maxP95Ms) }}</dd></div></dl>
              <h4>发布时正式冻结目标</h4>
              <dl class="draft-targets"><div><dt>最低精确率</dt><dd>{{ formatPercent(selectedVersion.bundle.releaseTargets.minPrecision) }}</dd></div><div><dt>最低召回率</dt><dd>{{ formatPercent(selectedVersion.bundle.releaseTargets.minRecall) }}</dd></div><div><dt>最高 p95</dt><dd>{{ formatMs(selectedVersion.bundle.releaseTargets.maxP95Ms) }}</dd></div></dl>
              <dl class="snapshot-identifiers"><div><dt>版本 ID</dt><dd>{{ selectedVersion.bundle.versionId }}</dd></div><div><dt>验证记录 ID</dt><dd>{{ selectedVersion.bundle.validationRunId }}</dd></div><div><dt>输入规格</dt><dd>{{ selectedVersion.bundle.input.width }} × {{ selectedVersion.bundle.input.height }} · {{ selectedVersion.bundle.input.requiresReference ? '需要参考图' : '无需参考图' }}</dd></div><div><dt>参考资产映射</dt><dd>{{ selectedVersion.bundle.references.length }} 张</dd></div><div v-if="selectedVersion.bundle.model"><dt>固定模型</dt><dd>{{ selectedVersion.bundle.model.sha256 }} · {{ (selectedVersion.bundle.model.byteLength / 1024 / 1024).toFixed(1) }} MiB · {{ selectedVersion.bundle.model.inputContract }}</dd></div><div><dt>版本包 SHA256</dt><dd>{{ selectedVersion.bundleHash }}</dd></div><div><dt>验证快照 SHA256</dt><dd>{{ selectedVersion.bundle.validationSnapshotHash }}</dd></div><div><dt>输入清单 SHA256</dt><dd>{{ selectedVersion.bundle.inputManifestSha256 }}</dd></div><div><dt>算法程序集 SHA256</dt><dd>{{ selectedVersion.bundle.algorithmAssemblySha256 }}</dd></div></dl>
              <details class="recipe-snapshot"><summary>查看冻结参数</summary><dl v-if="selectedVersion.bundle.definition.algorithm === 'Classical'" class="recipe-settings"><div v-for="(value, key) in selectedVersion.bundle.definition.settings" :key="key"><dt>{{ settingLabels[key] }}</dt><dd>{{ value }}</dd></div></dl><dl v-else class="recipe-settings"><div v-for="(value, key) in selectedVersion.bundle.definition.thresholds" :key="key"><dt>{{ thresholdLabels[key] }}</dt><dd>{{ value }}</dd></div></dl></details>
            </template>
          </div>
        </div>
      </div>
    </ElDrawer>
  </main>
</template>
