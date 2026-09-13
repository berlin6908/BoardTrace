<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElAlert, ElButton, ElCollapse, ElCollapseItem, ElEmpty, ElSkeleton, ElSwitch, ElTag } from 'element-plus'
import { ApiError, currentUser, getInspection, requestError } from '../api'
import { classLabel, decisionLabels, executionLabels, formatMs, formatTime, sourceLabel } from '../inspections'
import type { InspectionDetail } from '../inspections'
import EvidenceImage from './EvidenceImage.vue'

const props = defineProps<{ id: string }>()
const emit = defineEmits<{ 'session-expired': [] }>()
const detail = ref<InspectionDetail | null>(null)
const record = computed(() => detail.value?.inspection)
const loading = ref(true)
const error = ref('')
const showBoxes = ref(true)
const selectedDefect = ref<number | null>(null)
let detailRequest: AbortController | undefined

async function load() {
  detailRequest?.abort()
  const request = new AbortController()
  detailRequest = request
  detail.value = null
  selectedDefect.value = null
  loading.value = true
  error.value = ''
  try {
    const response = await getInspection(props.id, request.signal)
    if (!request.signal.aborted) detail.value = response
  } catch (cause) {
    if (!request.signal.aborted) {
      if (cause instanceof ApiError && cause.status === 401) emit('session-expired')
      else error.value = requestError(cause)
    }
  } finally {
    if (!request.signal.aborted) loading.value = false
  }
}

async function imageFailed() {
  const request = detailRequest
  try { await currentUser(request?.signal) }
  catch (cause) {
    if (!request?.signal.aborted && cause instanceof ApiError && cause.status === 401) emit('session-expired')
  }
}

const recipeText = computed(() => {
  if (!record.value) return ''
  try { return JSON.stringify(JSON.parse(record.value.recipeJson), null, 2) }
  catch { return record.value.recipeJson }
})

watch(() => props.id, load, { immediate: true })
onBeforeUnmount(() => detailRequest?.abort())
</script>

<template>
  <div v-if="loading" class="detail-loading" aria-live="polite" aria-busy="true"><span class="sr-only">正在读取检测档案</span><ElSkeleton :rows="12" animated /></div>
  <div v-else-if="error" class="state-panel" role="alert"><ElAlert title="未能读取检测档案" :description="error" type="error" show-icon :closable="false" /><ElButton type="primary" @click="load">重新读取</ElButton></div>
  <article v-else-if="detail && record" class="inspection-detail">
    <header class="detail-heading">
      <div><p class="eyebrow">产品编号</p><h2>{{ record.productId }}</h2><p class="record-id">{{ record.id }}</p></div>
      <ElTag :type="record.decision === 'Pass' ? 'success' : record.decision === 'Fail' ? 'danger' : 'info'" size="large" effect="light" round>{{ decisionLabels[record.decision] }}</ElTag>
    </header>

    <div :class="['source-banner', { constructed: record.sourceKind === 'ConstructedNormal' }]">
      <strong>{{ sourceLabel(record.sourceKind) }}</strong>
      <span v-if="record.sourceKind === 'ConstructedNormal'">参考图构造输入，仅验证检测流程，不代表真实良品。</span>
      <span v-else-if="record.sourceKind === 'Replay'">公开数据驱动的模拟记录，判定由图像算法产生。</span>
    </div>

    <ElAlert v-if="record.error" class="execution-error" :title="executionLabels[record.executionStatus]" :description="record.error" type="error" show-icon :closable="false" />

    <dl class="detail-metadata">
      <div><dt>检测工位</dt><dd>{{ record.stationId }}</dd></div>
      <div><dt>操作员</dt><dd>{{ record.operatorName }}</dd></div>
      <div><dt>输入样本</dt><dd>{{ record.sampleId }}</dd></div>
      <div><dt>执行状态</dt><dd>{{ executionLabels[record.executionStatus] }}</dd></div>
      <div><dt>检测耗时</dt><dd class="numeric">{{ formatMs(record.detectionMs) }}</dd></div>
      <div><dt>开始时间</dt><dd>{{ formatTime(record.startedAt) }}</dd></div>
      <div><dt>完成时间</dt><dd>{{ formatTime(record.completedAt) }}</dd></div>
      <div><dt>中央接收</dt><dd>{{ formatTime(detail.receivedAt) }}</dd></div>
      <div><dt>方案版本</dt><dd>{{ record.recipeId }}</dd></div>
    </dl>

    <section class="image-section" aria-labelledby="image-evidence-title">
      <div class="section-heading"><h3 id="image-evidence-title">图像证据</h3><ElSwitch v-model="showBoxes" active-text="显示缺陷区域" :disabled="!record.defects.length" /></div>
      <div class="image-pair">
        <figure><figcaption><strong>参考图</strong><span>{{ record.width }} × {{ record.height }}</span></figcaption><EvidenceImage :key="`${record.id}-reference`" :inspection-id="record.id" @failed="imageFailed" kind="reference" :available="detail.hasReferenceImage" :width="record.width" :height="record.height" /></figure>
        <figure><figcaption><strong>待检图</strong><span>{{ record.defects.length }} 个区域</span></figcaption><EvidenceImage :key="`${record.id}-tested`" :inspection-id="record.id" @failed="imageFailed" kind="tested" :available="detail.hasTestedImage" :width="record.width" :height="record.height" :defects="record.defects" :show-boxes="showBoxes" :selected-defect="selectedDefect" @select="selectedDefect = $event" /></figure>
      </div>
      <p class="section-note">图像为本次检测保存的原始证据。区域与编号来自算法输出。</p>
    </section>

    <section class="defects-section" aria-labelledby="defects-title">
      <div class="section-heading"><h3 id="defects-title">缺陷区域 <span class="record-count">{{ record.defects.length }}</span></h3><span class="section-note">坐标与面积单位：像素</span></div>
      <ElEmpty v-if="!record.defects.length" :image-size="56" :description="record.decision === 'Pass' ? '本次检测未检出缺陷区域' : '本次没有可显示的缺陷区域'" />
      <div v-else class="defect-list">
        <button v-for="(defect, index) in record.defects" :key="index" :class="['defect-item', { selected: selectedDefect === index }]" :aria-pressed="selectedDefect === index" @click="selectedDefect = selectedDefect === index ? null : index; showBoxes = true">
          <span class="defect-number">{{ index + 1 }}</span>
          <span class="defect-description"><strong>{{ classLabel(defect.classId) }}</strong><span>[{{ defect.box.map(value => value.toFixed(1)).join(', ') }}]</span></span>
          <span v-if="defect.classId !== null" class="defect-measure">置信度<strong>{{ (defect.score * 100).toFixed(1) }}%</strong></span>
          <span class="defect-measure">面积<strong>{{ defect.area }}</strong></span>
        </button>
      </div>
    </section>

    <ElCollapse class="technical-details">
      <ElCollapseItem title="方案参数与检测诊断" name="diagnostics">
        <p class="section-note">保存于本次检测的参数快照</p><pre>{{ recipeText }}</pre>
        <dl class="diagnostics"><div v-for="(value, name) in record.diagnostics" :key="name"><dt>{{ name }}</dt><dd>{{ value }}</dd></div></dl>
        <p class="section-note">中央内容指纹</p><code class="content-hash">{{ detail.contentHash }}</code>
      </ElCollapseItem>
    </ElCollapse>
    <p class="detail-footnote">时间按浏览器本地时区显示。机器原判随检测档案保留。</p>
  </article>
</template>
