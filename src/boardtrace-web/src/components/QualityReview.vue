<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElAlert, ElButton, ElInput, ElOption, ElSelect, ElSkeleton, ElTag } from 'element-plus'
import { ApiError, requestError } from '../api'
import { decisionLabels, executionLabels, formatTime } from '../inspections'
import type { InspectionRecord } from '../inspections'
import { dispositionLabels, getQualityDetails, submitReview } from '../quality'
import type { InspectionQualityDetails, ReviewDisposition } from '../quality'

const props = defineProps<{ record: InspectionRecord; canReview: boolean }>()
const emit = defineEmits<{ 'session-expired': []; 'open-inspection': [id: string]; reviewed: [] }>()
const details = ref<InspectionQualityDetails | null>(null)
const loading = ref(true)
const error = ref('')
const submitting = ref(false)
const submitError = ref('')
const disposition = ref<ReviewDisposition | ''>('')
const note = ref('')
let request: AbortController | undefined
let submitRequest: AbortController | undefined

const reviewable = computed(() => ['Production', 'Reinspection'].includes(props.record.purpose)
  && ['Completed', 'Failed', 'Interrupted'].includes(props.record.executionStatus))
const choices = computed<ReviewDisposition[]>(() => props.record.executionStatus === 'Completed'
  ? ['Accept', 'Reject', 'Rework'] : ['ResolveTechnicalIssue', 'Rework'])
const canSubmit = computed(() => props.canReview && reviewable.value && !details.value?.review && !submitting.value
  && choices.value.includes(disposition.value as ReviewDisposition) && !!note.value.trim() && note.value.trim().length <= 2000)

async function load() {
  request?.abort()
  submitRequest?.abort()
  const current = new AbortController()
  request = current
  details.value = null
  error.value = ''
  submitError.value = ''
  disposition.value = ''
  note.value = ''
  loading.value = true
  try { const result = await getQualityDetails(props.record.id, current.signal); if (!current.signal.aborted) details.value = result }
  catch (cause) {
    if (!current.signal.aborted) {
      if (cause instanceof ApiError && cause.status === 401) emit('session-expired')
      else error.value = requestError(cause)
    }
  } finally { if (!current.signal.aborted) loading.value = false }
}

async function submit() {
  if (!canSubmit.value) return
  const current = new AbortController()
  submitRequest = current
  const selected = disposition.value as ReviewDisposition
  const comment = note.value.trim()
  submitting.value = true
  submitError.value = ''
  try {
    const result = await submitReview(props.record.id, selected, comment, current.signal)
    if (!current.signal.aborted) { details.value = result; emit('reviewed') }
  } catch (cause) {
    if (!current.signal.aborted) {
      if (cause instanceof ApiError && cause.status === 401) emit('session-expired')
      else submitError.value = `${requestError(cause)}${cause instanceof ApiError ? '' : ' 请求可能已提交，请重新读取复核记录确认。'}`
    }
  } finally { submitting.value = false }
}

watch(() => props.record.id, load, { immediate: true })
onBeforeUnmount(() => { request?.abort(); submitRequest?.abort() })
</script>

<template>
  <section v-if="reviewable" class="quality-review">
    <div class="section-heading"><h3>人工质量复核</h3><ElButton :loading="loading" @click="load">刷新复核</ElButton></div>
    <p class="section-note">机器原判：{{ decisionLabels[record.decision] }} · {{ executionLabels[record.executionStatus] }}。人工处置单独保存，不修改原始检测结果。</p>
    <ElSkeleton v-if="loading" :rows="3" animated />
    <div v-else-if="error" class="state-panel"><ElAlert :title="error" type="error" show-icon :closable="false" /><ElButton @click="load">重试读取</ElButton></div>
    <template v-else-if="details">
      <div v-if="details.sourceReworkOrder" class="quality-relation"><strong>复检来源</strong><span>返工指令 {{ details.sourceReworkOrder.id }}</span><ElButton link type="primary" @click="emit('open-inspection', details.sourceReworkOrder.originalInspectionId)">查看原检测</ElButton></div>
      <div v-if="details.review" class="quality-final"><ElTag type="success">最终复核</ElTag><strong>{{ dispositionLabels[details.review.disposition] }}</strong><p>{{ details.review.note }}</p><small>{{ details.review.reviewedByName }} · {{ formatTime(details.review.reviewedAt) }}</small></div>
      <template v-else>
        <p class="section-note">尚无最终人工处置。{{ canReview ? '提交后不能编辑，请先核对上方双图、缺陷区域和机器原判。' : '只有质量工程师可以提交复核。' }}</p>
        <div v-if="canReview" class="quality-form">
          <label for="review-disposition">处置</label><ElSelect id="review-disposition" v-model="disposition" placeholder="选择处置" :disabled="submitting"><ElOption v-for="item in choices" :key="item" :label="dispositionLabels[item]" :value="item" /></ElSelect>
          <label for="review-note">复核意见（必填）</label><ElInput id="review-note" v-model="note" type="textarea" :rows="3" maxlength="2000" show-word-limit :disabled="submitting" placeholder="说明本次人工处置的依据" />
          <ElAlert v-if="disposition" :title="`即将提交：机器原判 ${decisionLabels[record.decision]}，人工处置 ${dispositionLabels[disposition]}`" type="warning" :closable="false" />
          <ElAlert v-if="submitError" :title="submitError" type="error" show-icon :closable="false"><ElButton link @click="load">重新读取复核记录</ElButton></ElAlert>
          <ElButton type="primary" :disabled="!canSubmit" :loading="submitting" @click="submit">提交最终复核</ElButton>
        </div>
      </template>
      <div v-if="details.reworkOrder" class="quality-relation"><strong>后续返工指令</strong><span>{{ details.reworkOrder.id }}</span><ElTag :type="details.reinspectionId ? 'success' : 'warning'">{{ details.reinspectionId ? '已有复检记录' : '等待复检' }}</ElTag><ElButton v-if="details.reinspectionId" link type="primary" @click="emit('open-inspection', details.reinspectionId)">查看复检</ElButton></div>
    </template>
  </section>
</template>
