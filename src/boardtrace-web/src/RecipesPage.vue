<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElAlert, ElButton, ElDialog, ElEmpty, ElOption, ElProgress, ElSelect, ElSkeleton, ElTag } from 'element-plus'
import { ApiError, requestError } from './api'
import type { CurrentUser } from './api'
import { formatMs, formatTime } from './inspections'
import { formatPercent, getValidation, isActiveRun, listDrafts, listValidations, saveDraft, settingLabels, startValidation, validationLabels } from './recipes'
import type { RecipeDraft, SaveRecipeDraft, ValidationRun } from './recipes'
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
const lifetime = new AbortController()
let listRequest: AbortController | undefined
let historyRequest: AbortController | undefined
let runRequest: AbortController | undefined
let pollTimer: ReturnType<typeof setTimeout> | undefined
const busy = computed(() => saving.value || starting.value)
const currentRunActive = computed(() => runs.value.some(run => isActiveRun(run) && run.snapshot.snapshotHash === selectedDraft.value?.snapshotHash))

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
  if (!canEdit.value || !selectedDraft.value || starting.value) return
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

onMounted(loadDrafts)
onBeforeUnmount(() => { lifetime.abort(); listRequest?.abort(); historyRequest?.abort(); stopTracking() })
</script>

<template>
  <main class="workspace recipes-workspace">
    <div class="page-heading"><div><p class="eyebrow">工艺开发 · 固定验证集</p><h1>方案验证</h1><p class="subtitle">保存经典检测草稿，在 200 张验证图像上检查定位效果与耗时。</p></div><ElButton v-if="canEdit" type="primary" :disabled="busy || loading" @click="editDraft(null)">新建草稿</ElButton></div>
    <p v-if="!canEdit" class="recipe-access-note">当前角色可查看方案与验证报告；草稿维护和启动验证由工艺工程师执行。</p>
    <div v-if="loading" class="records-panel list-loading" aria-busy="true"><ElSkeleton :rows="6" animated /></div>
    <div v-else-if="listError" class="records-panel state-panel"><ElAlert :title="listError" type="error" :closable="false" show-icon /><ElButton @click="loadDrafts">重新连接</ElButton></div>
    <div v-else-if="!drafts.length" class="records-panel"><ElEmpty description="尚无方案草稿"><ElButton v-if="canEdit" type="primary" @click="editDraft(null)">创建第一个草稿</ElButton><p v-else class="empty-help">工艺工程师创建草稿后，可在此查看参数与真实验证结果。</p></ElEmpty></div>
    <div v-else class="recipe-layout">
      <aside class="records-panel recipe-list" aria-label="方案草稿列表"><div class="results-heading"><h2>草稿 <span class="record-count">{{ drafts.length }}</span></h2><ElButton link :disabled="busy" @click="loadDrafts">刷新</ElButton></div>
        <button v-for="draft in drafts" :key="draft.id" type="button" class="recipe-choice" :class="{ selected: selectedId === draft.id }" :aria-pressed="selectedId === draft.id" :disabled="busy" @click="selectDraft(draft.id)"><strong>{{ draft.name }}</strong><span>Classical · 经典定位</span><small>{{ formatTime(draft.updatedAt) }}</small></button>
      </aside>
      <div v-if="selectedDraft" class="recipe-content">
        <section class="records-panel recipe-draft">
          <div class="section-heading"><div><h2>{{ selectedDraft.name }}</h2><p class="section-note">草稿 · 经典定位 · {{ formatTime(selectedDraft.updatedAt) }} 保存</p></div><ElButton v-if="canEdit" :disabled="busy" @click="editDraft(selectedDraft)">编辑草稿</ElButton></div>
          <dl class="draft-targets"><div><dt>最低精确率</dt><dd>{{ formatPercent(selectedDraft.targets.minPrecision) }}</dd></div><div><dt>最低召回率</dt><dd>{{ formatPercent(selectedDraft.targets.minRecall) }}</dd></div><div><dt>最高 p95</dt><dd>{{ formatMs(selectedDraft.targets.maxP95Ms) }}</dd></div></dl>
          <p class="section-note">以上是当前草稿的验收目标，保存时绑定固定验证输入清单；每次验证保留当时的参数和目标。</p>
          <details class="recipe-snapshot"><summary>查看当前参数与输入清单</summary><dl class="recipe-settings"><div v-for="(value, key) in selectedDraft.settings" :key="key"><dt>{{ settingLabels[key] }}</dt><dd>{{ value }}</dd></div></dl><dl class="snapshot-identifiers"><div><dt>草稿快照</dt><dd>{{ selectedDraft.snapshotHash }}</dd></div><div><dt>固定输入清单 SHA256</dt><dd>{{ selectedDraft.dataManifestSha256 }}</dd></div></dl></details>
          <div v-if="canEdit" class="validation-actions"><ElButton type="primary" :loading="starting" :disabled="saving || editorOpen || currentRunActive || historyLoading || Boolean(historyError)" @click="validateDraft">{{ currentRunActive ? '当前草稿正在验证' : '启动验证 · 200 张' }}</ElButton><span class="section-note">使用已保存草稿，后台执行真实图像检测。</span></div>
        </section>
        <section class="records-panel validation-panel">
          <div class="validation-toolbar"><h2>验证记录</h2><div class="validation-history"><label class="sr-only" for="validation-history">选择历史验证</label><ElSelect v-if="runs.length" id="validation-history" :model-value="selectedRun?.id" :disabled="starting" aria-label="选择历史验证" @update:model-value="selectRun"><ElOption v-for="run in runs" :key="run.id" :value="run.id" :label="`${formatTime(run.createdAt)} · ${validationLabels[run.status]} · ${run.snapshot.snapshotHash.slice(0, 8)}`" /></ElSelect><ElButton :disabled="busy || historyLoading" @click="loadRuns(selectedDraft.id)">刷新记录</ElButton></div></div>
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
          </template>
          <ElEmpty v-else-if="!historyLoading && !historyError" description="尚无验证记录"><p class="empty-help">工艺工程师启动验证后，这里显示进度与逐样本报告。</p></ElEmpty>
        </section>
      </div>
    </div>
    <p class="page-footnote">当前页面用于开发方案验证，目标针对各草稿保存。验证输入属于 validation 集合。</p>
    <ElDialog v-model="editorOpen" :title="editingDraft ? '编辑方案草稿' : '新建方案草稿'" width="min(760px, 94vw)" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving" destroy-on-close class="recipe-editor-dialog"><DraftEditor :key="editorKey" :draft="editingDraft" :saving="saving" :error="saveError" @save="save" @cancel="editorOpen = false" /></ElDialog>
  </main>
</template>
