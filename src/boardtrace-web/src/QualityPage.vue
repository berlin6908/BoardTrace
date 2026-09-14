<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElAlert, ElButton, ElDrawer, ElEmpty, ElInput, ElOption, ElPagination, ElSelect, ElSkeleton, ElTable, ElTableColumn, ElTag } from 'element-plus'
import { ApiError, requestError } from './api'
import type { CurrentUser } from './api'
import { decisionLabels, executionLabels, formatTime, purposeLabels } from './inspections'
import type { InspectionRecord } from './inspections'
import { listQualityQueue } from './quality'
import type { QualityQueuePage } from './quality'
import { listBatches } from './batches'
import type { BatchSummary } from './batches'
import InspectionDetail from './components/InspectionDetail.vue'
import QualityReview from './components/QualityReview.vue'
import './quality.css'

const props = defineProps<{ user: CurrentUser }>()
const emit = defineEmits<{ 'session-expired': [] }>()
const query = reactive({ batchId: '', stationId: '', page: 1, pageSize: 20 })
const result = ref<QualityQueuePage | null>(null)
const loading = ref(true)
const error = ref('')
const batches = ref<BatchSummary[]>([])
const batchesLoading = ref(false)
const batchesError = ref('')
const selectedId = ref('')
const selectedRecord = ref<InspectionRecord | null>(null)
let request: AbortController | undefined
let batchesRequest: AbortController | undefined

async function loadBatches() {
  batchesRequest?.abort()
  const current = new AbortController()
  batchesRequest = current
  batchesLoading.value = true
  batchesError.value = ''
  try { const response = await listBatches(current.signal); if (!current.signal.aborted) batches.value = response }
  catch (cause) {
    if (!current.signal.aborted) {
      if (cause instanceof ApiError && cause.status === 401) emit('session-expired')
      else batchesError.value = requestError(cause)
    }
  } finally { if (!current.signal.aborted) batchesLoading.value = false }
}

async function load() {
  request?.abort()
  const current = new AbortController()
  request = current
  loading.value = true
  error.value = ''
  try { const response = await listQualityQueue(query, current.signal); if (!current.signal.aborted) result.value = response }
  catch (cause) {
    if (!current.signal.aborted) {
      if (cause instanceof ApiError && cause.status === 401) emit('session-expired')
      else error.value = requestError(cause)
    }
  } finally { if (!current.signal.aborted) loading.value = false }
}
function search() { query.page = 1; void load() }
function openInspection(id: string) { selectedRecord.value = null; selectedId.value = id }
function closeDrawer(open: boolean) { if (!open) { selectedId.value = ''; selectedRecord.value = null } }
function recordLoaded(record: InspectionRecord) { if (record.id === selectedId.value) selectedRecord.value = record }
function reviewed() { void load() }
onMounted(() => { void load(); void loadBatches() })
onBeforeUnmount(() => { request?.abort(); batchesRequest?.abort() })
</script>

<template>
  <main class="workspace quality-workspace">
    <div class="page-heading"><div><p class="eyebrow">质量工程师 · 人工处置</p><h1>待复核检测</h1><p class="subtitle">机器原判、图像证据和人工最终处置分别留档。</p></div><ElButton :loading="loading" @click="load">刷新队列</ElButton></div>
    <section class="records-panel">
      <form class="filters" @submit.prevent="search">
        <div class="filter-field"><label for="quality-batch">批号</label><ElSelect id="quality-batch" v-model="query.batchId" filterable clearable :loading="batchesLoading" :disabled="!!batchesError" placeholder="全部批次"><ElOption v-for="item in batches" :key="item.batch.id" :label="`${item.batch.batchNumber} · ${item.batch.productType}`" :value="item.batch.id" /></ElSelect><span v-if="batchesError" class="section-note">{{ batchesError }} <ElButton link @click="loadBatches">重试批次</ElButton></span></div>
        <div class="filter-field"><label for="quality-station">工位编号</label><ElInput id="quality-station" v-model="query.stationId" clearable placeholder="按工位筛选" /></div>
        <div class="filter-actions"><ElButton type="primary" native-type="submit" :loading="loading">查询</ElButton><ElButton @click="query.batchId = ''; query.stationId = ''; search()">重置</ElButton></div>
      </form>
      <div class="results-heading"><h2>待复核队列 <span v-if="result && !error" class="record-count">{{ result.total }}</span></h2><span class="section-note">生产缺陷、技术异常与全部复检结果；生产通过记录可在检测追溯中主动复核。</span></div>
      <div v-if="error" class="state-panel"><ElAlert :title="error" type="error" show-icon :closable="false" /><ElButton @click="load">重试读取</ElButton></div>
      <div v-else-if="loading" class="list-loading"><ElSkeleton :rows="6" animated /></div>
      <ElEmpty v-else-if="!result?.items.length" description="当前没有待复核检测" />
      <ElTable v-else :data="result.items" row-key="inspectionId" class="inspection-table">
        <ElTableColumn label="产品 / 样本" min-width="160"><template #default="{ row }"><button class="record-link" @click="openInspection(row.inspectionId)">{{ row.productId }}</button><span class="secondary-line">{{ row.sampleId }}</span></template></ElTableColumn>
        <ElTableColumn label="批次 / 工位" min-width="150"><template #default="{ row }">{{ row.batchNumber }}<span class="secondary-line">{{ row.stationId }}</span></template></ElTableColumn>
        <ElTableColumn label="用途" width="105"><template #default="{ row }">{{ purposeLabels[row.purpose as keyof typeof purposeLabels] }}</template></ElTableColumn>
        <ElTableColumn label="机器原判" min-width="145"><template #default="{ row }"><ElTag :type="row.decision === 'Pass' ? 'success' : row.decision === 'Fail' ? 'danger' : 'info'">{{ decisionLabels[row.decision as keyof typeof decisionLabels] }}</ElTag><span v-if="row.executionStatus !== 'Completed'" class="secondary-line">{{ executionLabels[row.executionStatus as keyof typeof executionLabels] }}</span></template></ElTableColumn>
        <ElTableColumn label="开始时间" min-width="165"><template #default="{ row }">{{ formatTime(row.startedAt) }}</template></ElTableColumn>
        <ElTableColumn label="" width="78"><template #default="{ row }"><ElButton link type="primary" @click="openInspection(row.inspectionId)">复核</ElButton></template></ElTableColumn>
      </ElTable>
      <div v-if="result && result.total > 0 && !error" class="pagination-row"><ElPagination v-model:current-page="query.page" :page-size="query.pageSize" :total="result.total" :disabled="loading" layout="prev, pager, next" background @current-change="load" /></div>
    </section>
    <ElDrawer :model-value="!!selectedId" title="检测证据与人工复核" class="inspection-drawer" size="min(1220px, 96vw)" destroy-on-close @update:model-value="closeDrawer">
      <InspectionDetail v-if="selectedId" :key="selectedId" :id="selectedId" @loaded="recordLoaded" @session-expired="emit('session-expired')" />
      <QualityReview v-if="selectedRecord && selectedRecord.id === selectedId" :key="selectedId" :record="selectedRecord" :can-review="props.user.roles.includes('QualityEngineer')" @session-expired="emit('session-expired')" @open-inspection="openInspection" @reviewed="reviewed" />
    </ElDrawer>
  </main>
</template>
