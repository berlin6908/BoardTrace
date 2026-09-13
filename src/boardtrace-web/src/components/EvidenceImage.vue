<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElButton } from 'element-plus'
import type { DefectBox } from '../inspections'

const props = defineProps<{
  inspectionId: string
  kind: 'tested' | 'reference'
  available: boolean
  width: number
  height: number
  defects?: DefectBox[]
  showBoxes?: boolean
  selectedDefect?: number | null
}>()
const emit = defineEmits<{ select: [index: number] }>()
const loaded = ref(false)
const failed = ref(false)
const attempt = ref(0)
const imageUrl = computed(() => `/api/inspections/${encodeURIComponent(props.inspectionId)}/images/${props.kind}?attempt=${attempt.value}`)
const aspectRatio = computed(() => props.width > 0 && props.height > 0 ? `${props.width} / ${props.height}` : '1')

function retry() {
  failed.value = false
  loaded.value = false
  attempt.value++
}
</script>

<template>
  <div class="evidence-image" :style="{ aspectRatio }">
    <div v-if="!available" class="image-message">本次未保存{{ kind === 'tested' ? '待检' : '参考' }}图像</div>
    <template v-else>
      <img v-show="loaded && !failed" :key="imageUrl" :src="imageUrl" :alt="kind === 'tested' ? '检测时保存的待检原图' : '检测时保存的参考原图'" @load="loaded = true" @error="failed = true" />
      <div v-if="failed" class="image-message" role="alert"><span>图像读取失败</span><ElButton size="small" @click="retry">重试图像</ElButton></div>
      <div v-else-if="!loaded" class="image-message" role="status">正在读取原图…</div>
      <svg v-if="loaded && !failed && showBoxes && defects?.length" class="defect-overlay" :viewBox="`0 0 ${width} ${height}`" :aria-label="`${defects.length} 个算法检测区域`">
        <g v-for="(defect, index) in defects" :key="index" class="defect-box" :class="{ selected: selectedDefect === index }" tabindex="0" role="button" :aria-label="`选择缺陷区域 ${index + 1}`" @click="emit('select', index)" @keydown.enter.prevent="emit('select', index)" @keydown.space.prevent="emit('select', index)">
          <rect :x="defect.box[0]" :y="defect.box[1]" :width="defect.box[2] - defect.box[0]" :height="defect.box[3] - defect.box[1]" vector-effect="non-scaling-stroke" />
          <text :x="defect.box[0] + 3" :y="Math.max(15, defect.box[1] - 5)" paint-order="stroke">{{ index + 1 }}</text>
        </g>
      </svg>
    </template>
  </div>
</template>
