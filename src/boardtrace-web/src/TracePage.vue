<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElAlert, ElButton, ElDrawer, ElEmpty, ElIcon, ElInput, ElOption, ElPagination, ElSelect, ElSkeleton, ElTable, ElTableColumn, ElTag } from 'element-plus'
import { ArrowRight, Refresh, Search } from '@element-plus/icons-vue'
import InspectionDetail from './components/InspectionDetail.vue'
import { ApiError, listInspections, requestError } from './api'
import { decisionLabels, executionLabels, formatMs, formatTime, purposeLabels, sourceLabel } from './inspections'
import type { Decision, InspectionPage } from './inspections'

const emit = defineEmits<{ 'session-expired': [] }>()
const params = new URLSearchParams(window.location.search)
const decision = params.get('decision') ?? ''
const query = reactive({
  stationId: params.get('stationId') ?? '',
  productId: params.get('productId') ?? '',
  decision: (['Pass', 'Fail', 'NotEvaluated'].includes(decision) ? decision : '') as Decision | '',
  page: Math.max(1, Number(params.get('page')) || 1),
  pageSize: 20,
})
const page = ref<InspectionPage | null>(null)
const loading = ref(true)
const error = ref('')
const loadedAt = ref<string | null>(null)
const selectedId = ref(params.get('inspection') ?? '')
const drawerOpen = ref(Boolean(selectedId.value))
let listRequest: AbortController | undefined

function updateUrl() {
  const next = new URLSearchParams()
  if (query.stationId) next.set('stationId', query.stationId)
  if (query.productId) next.set('productId', query.productId)
  if (query.decision) next.set('decision', query.decision)
  if (query.page > 1) next.set('page', String(query.page))
  if (drawerOpen.value && selectedId.value) next.set('inspection', selectedId.value)
  window.history.replaceState(null, '', `${window.location.pathname}${next.size ? `?${next}` : ''}`)
}

async function load() {
  listRequest?.abort()
  const request = new AbortController()
  listRequest = request
  loading.value = true
  error.value = ''
  query.stationId = query.stationId.trim()
  query.productId = query.productId.trim()
  updateUrl()
  try {
    const response = await listInspections(query, request.signal)
    if (request.signal.aborted) return
    page.value = response
    loadedAt.value = new Date().toISOString()
  } catch (cause) {
    if (!request.signal.aborted) {
      if (cause instanceof ApiError && cause.status === 401) emit('session-expired')
      else error.value = requestError(cause)
    }
  } finally {
    if (!request.signal.aborted) loading.value = false
  }
}

function search() {
  query.page = 1
  void load()
}

function clearFilters() {
  query.stationId = ''
  query.productId = ''
  query.decision = ''
  search()
}

function openInspection(id: string) {
  selectedId.value = id
  drawerOpen.value = true
  updateUrl()
}

onMounted(load)
onBeforeUnmount(() => listRequest?.abort())
</script>

<template>
      <main class="workspace">
        <div class="page-heading">
          <div><p class="eyebrow">中央质量平台</p><h1>检测追溯</h1><p class="subtitle">查看工位检测档案与原始图像证据</p></div>
          <ElButton :icon="Refresh" :loading="loading" @click="load">刷新记录</ElButton>
        </div>

        <section class="records-panel" aria-label="检测记录">
          <form class="filters" @submit.prevent="search">
            <div class="filter-field"><label for="station-filter">工位编号</label><ElInput id="station-filter" v-model="query.stationId" placeholder="输入工位编号" clearable /></div>
            <div class="filter-field product-filter"><label for="product-filter">产品编号</label><ElInput id="product-filter" v-model="query.productId" placeholder="输入产品编号" clearable /></div>
            <div class="filter-field"><label for="decision-filter">质量判定</label><ElSelect id="decision-filter" v-model="query.decision" placeholder="全部判定" clearable><ElOption label="通过" value="Pass" /><ElOption label="缺陷" value="Fail" /><ElOption label="未判定" value="NotEvaluated" /></ElSelect></div>
            <div class="filter-actions"><ElButton type="primary" native-type="submit" :icon="Search" :loading="loading">查询</ElButton><ElButton @click="clearFilters">重置</ElButton></div>
          </form>

          <div class="results-heading">
            <h2>检测档案 <span v-if="page && !error" class="record-count">{{ page.total.toLocaleString() }}</span></h2>
            <span v-if="loadedAt && !error" class="sync-caption">读取于 {{ formatTime(loadedAt) }} · 本地时区</span>
          </div>

          <div v-if="error" class="state-panel" role="alert">
            <ElAlert title="未能读取检测记录" :description="error" type="error" show-icon :closable="false" />
            <ElButton type="primary" :icon="Refresh" @click="load">重新连接</ElButton>
          </div>
          <div v-else-if="loading" class="list-loading" aria-live="polite" aria-busy="true"><span class="sr-only">正在读取检测记录</span><ElSkeleton :rows="7" animated /></div>
          <ElEmpty v-else-if="!page?.items.length" :description="query.stationId || query.productId || query.decision ? '没有符合筛选条件的检测记录' : '中央尚未收到检测记录'">
            <p class="empty-help">{{ query.stationId || query.productId || query.decision ? '可调整筛选条件后重新查询。' : '工位完成检测并同步后，档案会显示在这里。' }}</p>
            <ElButton v-if="query.stationId || query.productId || query.decision" @click="clearFilters">清除筛选</ElButton>
          </ElEmpty>
          <ElTable v-else :data="page.items" row-key="id" class="inspection-table" :row-class-name="({ row }) => `decision-row-${row.decision}`">
            <ElTableColumn label="产品 / 样本" min-width="195"><template #default="{ row }"><button class="record-link" @click="openInspection(row.id)">{{ row.productId }}<ElIcon><ArrowRight /></ElIcon></button><span class="secondary-line">样本 {{ row.sampleId }}</span></template></ElTableColumn>
            <ElTableColumn label="开始时间" min-width="175"><template #default="{ row }"><span class="time-cell">{{ formatTime(row.startedAt) }}</span></template></ElTableColumn>
            <ElTableColumn prop="stationId" label="工位" min-width="115" />
            <ElTableColumn label="用途 / 批次" min-width="155"><template #default="{ row }"><strong>{{ purposeLabels[row.purpose as keyof typeof purposeLabels] }}</strong><span v-if="row.batchId" class="secondary-line">批次 {{ row.batchId.slice(0, 8) }}<template v-if="row.productionSequence"> · #{{ row.productionSequence }}</template></span></template></ElTableColumn>
            <ElTableColumn label="质量判定" min-width="125"><template #default="{ row }"><ElTag :type="row.decision === 'Pass' ? 'success' : row.decision === 'Fail' ? 'danger' : 'info'" effect="light" round>{{ decisionLabels[row.decision as Decision] }}</ElTag><span v-if="row.executionStatus !== 'Completed'" class="secondary-line">{{ executionLabels[row.executionStatus as keyof typeof executionLabels] }}</span></template></ElTableColumn>
            <ElTableColumn prop="defectCount" label="缺陷区域" width="100" align="right" />
            <ElTableColumn label="检测耗时" width="115" align="right"><template #default="{ row }"><span class="numeric">{{ formatMs(row.detectionMs) }}</span></template></ElTableColumn>
            <ElTableColumn label="输入来源" min-width="185"><template #default="{ row }"><span :class="['source-label', { constructed: row.sourceKind === 'ConstructedNormal' }]">{{ sourceLabel(row.sourceKind) }}</span></template></ElTableColumn>
            <ElTableColumn label="" width="80" fixed="right"><template #default="{ row }"><ElButton link type="primary" :aria-label="`查看 ${row.productId} 的检测档案`" @click="openInspection(row.id)">查看</ElButton></template></ElTableColumn>
          </ElTable>

          <div v-if="page && !error && page.total > 0" class="pagination-row"><span class="pagination-caption">按检测开始时间倒序</span><ElPagination v-model:current-page="query.page" :page-size="query.pageSize" :total="page.total" :disabled="loading" :pager-count="5" layout="prev, pager, next" background @current-change="load" /></div>
        </section>

        <footer class="page-footnote"><span class="footnote-mark">i</span>检测对象为单个 PCB 视野；回放与构造样例用于模拟验证，不能据此推算真实产线良率。</footer>
      </main>

      <ElDrawer v-model="drawerOpen" title="检测档案" class="inspection-drawer" size="min(1220px, 96vw)" destroy-on-close @update:model-value="updateUrl">
        <InspectionDetail v-if="drawerOpen && selectedId" :id="selectedId" @session-expired="emit('session-expired')" />
      </ElDrawer>
</template>
