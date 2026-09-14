<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { ElAlert, ElButton, ElEmpty, ElSkeleton, ElTag } from 'element-plus'
import { ApiError, requestError } from './api'
import { formatTime } from './inspections'
import { listStationRuntime } from './batches'
import type { StationRuntimeView } from './batches'
import './stations.css'

const emit = defineEmits<{ 'session-expired': [] }>()
const stations = ref<StationRuntimeView[]>([])
const loading = ref(true)
const error = ref('')
let request: AbortController | undefined

async function load() {
  request?.abort()
  const current = new AbortController()
  request = current
  loading.value = true
  error.value = ''
  try { const result = await listStationRuntime(current.signal); if (!current.signal.aborted) stations.value = result }
  catch (cause) {
    if (!current.signal.aborted) {
      if (cause instanceof ApiError && cause.status === 401) emit('session-expired')
      else error.value = requestError(cause)
    }
  } finally { if (!current.signal.aborted) loading.value = false }
}
onMounted(load)
onBeforeUnmount(() => request?.abort())
</script>

<template>
  <main class="workspace stations-workspace">
    <div class="page-heading"><div><p class="eyebrow">工位现场 · 中央接收状态</p><h1>工位概览</h1><p class="subtitle">在线状态只按工位设备最近上报判断；检测上传不能代替现场上报。</p></div><ElButton :loading="loading" @click="load">刷新概览</ElButton></div>
    <div v-if="error" class="state-panel"><ElAlert :title="error" type="error" :closable="false" show-icon /><ElButton @click="load">重试读取</ElButton></div>
    <div v-else-if="loading" class="list-loading"><ElSkeleton :rows="7" animated /></div>
    <ElEmpty v-else-if="!stations.length" description="尚无注册工位" />
    <div v-else class="station-grid">
      <article v-for="station in stations" :key="station.stationId" class="station-card">
        <div class="station-card-heading"><div><h2>{{ station.displayName }}</h2><span>{{ station.stationId }}</span></div><ElTag :type="!station.runtime ? 'info' : station.isOnline ? 'success' : 'danger'">{{ !station.runtime ? '尚无工位上报' : station.isOnline ? '在线' : '离线' }}</ElTag></div>
        <template v-if="station.runtime">
          <dl class="station-facts"><div><dt>当前批次</dt><dd>{{ station.batchNumber || station.runtime.batchId || '无活动批次' }}</dd></div><div><dt>工位阶段</dt><dd>{{ station.runtime.state }}</dd></div><div><dt>现场首件 / 生产 / 复检</dt><dd>{{ station.runtime.firstArticleCount }} / {{ station.runtime.productionCount }} / {{ station.runtime.reinspectionCount }}</dd></div><div><dt>待上传档案</dt><dd>{{ station.runtime.pendingUploads }}</dd></div><div><dt>中央最近接收现场状态</dt><dd>{{ formatTime(station.receivedAt) }}</dd></div></dl>
          <div class="station-alarms"><ElTag v-if="station.runtime.pendingUploads > 0" type="warning">{{ station.runtime.pendingUploads }} 条待上传</ElTag><ElTag v-if="station.runtime.hasStartedInspection" type="warning">存在未结束检测</ElTag><ElTag v-if="station.runtime.hasUnacknowledgedPlc" type="warning">PLC 结果待确认</ElTag><ElAlert v-if="station.runtime.alarm" :title="station.runtime.alarm" type="warning" :closable="false" show-icon /></div>
        </template>
        <p v-else class="section-note">工位尚未发送现场快照，无法判断当前批次或积压。</p>
      </article>
    </div>
    <p class="page-footnote">工位显示的是最近一份设备上报；质量关闭条件由中央在提交时重新核对。</p>
  </main>
</template>
