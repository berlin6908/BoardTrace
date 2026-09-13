<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { ElAlert, ElButton, ElInput, ElInputNumber } from 'element-plus'
import { defaultClassicalSettings, settingLabels } from '../recipes'
import type { ClassicalSettings, RecipeDraft, SaveRecipeDraft } from '../recipes'

const props = defineProps<{ draft: RecipeDraft | null; saving: boolean; error: string }>()
const emit = defineEmits<{ save: [draft: SaveRecipeDraft]; cancel: [] }>()
const name = ref(props.draft?.name ?? '')
const settings = reactive({ ...(props.draft?.settings ?? defaultClassicalSettings) })
const targets = reactive({
  precision: props.draft ? props.draft.targets.minPrecision * 100 : undefined,
  recall: props.draft ? props.draft.targets.minRecall * 100 : undefined,
  p95: props.draft?.targets.maxP95Ms,
})
const fields: { key: keyof ClassicalSettings; min: number; max?: number; step: number; precision: number }[] = [
  { key: 'binarizationThreshold', min: 0, max: 255, step: 1, precision: 0 },
  { key: 'edgeTolerance', min: 0, max: 10, step: 1, precision: 0 },
  { key: 'minimumArea', min: 1, step: 1, precision: 0 },
  { key: 'closingSize', min: 1, max: 31, step: 2, precision: 0 },
  { key: 'boxPadding', min: 0, max: 100, step: 1, precision: 0 },
  { key: 'maximumTranslation', min: 0, step: 0.01, precision: 2 },
  { key: 'minimumAlignmentResponse', min: 0, max: 1, step: 0.001, precision: 3 },
]
const validationError = ref('')
const valid = computed(() => name.value.trim().length > 0 && name.value.trim().length <= 200
  && fields.every(field => typeof settings[field.key] === 'number' && Number.isFinite(settings[field.key])
    && settings[field.key] >= field.min && (field.max === undefined || settings[field.key] <= field.max))
  && settings.closingSize % 2 === 1
  && typeof targets.precision === 'number' && Number.isFinite(targets.precision) && targets.precision >= 0 && targets.precision <= 100
  && typeof targets.recall === 'number' && Number.isFinite(targets.recall) && targets.recall >= 0 && targets.recall <= 100
  && typeof targets.p95 === 'number' && Number.isFinite(targets.p95) && targets.p95 > 0)

function submit() {
  validationError.value = ''
  if (!valid.value) {
    validationError.value = '请填写方案名称、全部参数和三项验收目标；闭运算核尺寸必须为奇数。'
    return
  }
  emit('save', { name: name.value.trim(), settings: { ...settings }, targets: {
    minPrecision: targets.precision! / 100, minRecall: targets.recall! / 100, maxP95Ms: targets.p95!,
  } })
}
</script>

<template>
  <form class="draft-editor" @submit.prevent="submit">
    <ElAlert v-if="error || validationError" :title="error || validationError" type="error" :closable="false" show-icon role="alert" />
    <div class="filter-field recipe-name-field">
      <label for="recipe-name">方案名称</label>
      <ElInput id="recipe-name" v-model="name" maxlength="200" show-word-limit :disabled="saving" placeholder="例如：PCB 开发定位方案" />
    </div>
    <section>
      <h3>经典定位算法 · Classical</h3>
      <p class="section-note">参考图配准、差异与区域定位；初始参数沿用当前经典检测设置。</p>
      <div class="recipe-form-grid">
        <div v-for="field in fields" :key="field.key" class="filter-field">
          <label :for="`setting-${field.key}`">{{ settingLabels[field.key] }}</label>
          <ElInputNumber :id="`setting-${field.key}`" v-model="settings[field.key]" :min="field.min" :max="field.max" :step="field.step" :precision="field.precision" :disabled="saving" controls-position="right" />
        </div>
      </div>
    </section>
    <section>
      <h3>本草稿验收目标</h3>
      <p class="section-note">请明确填写。目标随验证快照保存，用于判断本次方案是否达标；这不是全项目冻结标准。</p>
      <div class="recipe-form-grid">
        <div class="filter-field"><label for="target-precision">最低精确率（%）</label><ElInputNumber id="target-precision" v-model="targets.precision" :min="0" :max="100" :step="0.01" :precision="2" :disabled="saving" controls-position="right" /></div>
        <div class="filter-field"><label for="target-recall">最低召回率（%）</label><ElInputNumber id="target-recall" v-model="targets.recall" :min="0" :max="100" :step="0.01" :precision="2" :disabled="saving" controls-position="right" /></div>
        <div class="filter-field"><label for="target-p95">最高 p95 耗时（ms）</label><ElInputNumber id="target-p95" v-model="targets.p95" :min="0.01" :step="0.01" :precision="2" :disabled="saving" controls-position="right" /></div>
      </div>
    </section>
    <p class="section-note">保存后可在固定的 200 张 validation 图像上执行真实检测，参考图由固定清单逐图绑定。</p>
    <div class="editor-actions"><ElButton :disabled="saving" @click="emit('cancel')">取消</ElButton><ElButton type="primary" native-type="submit" :loading="saving">保存草稿</ElButton></div>
  </form>
</template>
