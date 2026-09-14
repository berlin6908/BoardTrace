<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { ElAlert, ElButton, ElInput, ElInputNumber, ElOption, ElSelect } from 'element-plus'
import { algorithmLabels, defaultClassicalSettings, settingLabels, thresholdLabels } from '../recipes'
import type { ClassicalSettings, RecipeDraft, RecipeModelSummary, RecipeScoreThresholds, SaveRecipeDraft } from '../recipes'

const props = defineProps<{ draft: RecipeDraft | null; models: RecipeModelSummary[]; modelsLoading: boolean; modelsError: string; saving: boolean; error: string }>()
const emit = defineEmits<{ save: [draft: SaveRecipeDraft]; cancel: []; 'refresh-models': [] }>()
const name = ref(props.draft?.name ?? '')
const algorithm = ref<'Classical' | 'PairedOnnx'>(props.draft?.definition.algorithm ?? 'Classical')
const settings = reactive({ ...(props.draft?.definition.algorithm === 'Classical' ? props.draft.definition.settings : defaultClassicalSettings) })
const modelSha256 = ref(props.draft?.definition.algorithm === 'PairedOnnx' ? props.draft.definition.modelSha256 : '')
const thresholds = reactive<Record<keyof RecipeScoreThresholds, number | undefined>>(props.draft?.definition.algorithm === 'PairedOnnx'
  ? { ...props.draft.definition.thresholds } : { open: undefined, short: undefined, mousebite: undefined, spur: undefined, copper: undefined, pinHole: undefined })
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
const thresholdKeys: (keyof RecipeScoreThresholds)[] = ['open', 'short', 'mousebite', 'spur', 'copper', 'pinHole']
const validationError = ref('')
const valid = computed(() => name.value.trim().length > 0 && name.value.trim().length <= 200
  && (algorithm.value === 'Classical' ? fields.every(field => typeof settings[field.key] === 'number' && Number.isFinite(settings[field.key])
    && settings[field.key] >= field.min && (field.max === undefined || settings[field.key] <= field.max)) && settings.closingSize % 2 === 1
    : props.models.some(model => model.sha256 === modelSha256.value) && thresholdKeys.every(key => typeof thresholds[key] === 'number' && Number.isFinite(thresholds[key]) && thresholds[key] >= 0 && thresholds[key] <= 1))
  && typeof targets.precision === 'number' && Number.isFinite(targets.precision) && targets.precision >= 0 && targets.precision <= 100
  && typeof targets.recall === 'number' && Number.isFinite(targets.recall) && targets.recall >= 0 && targets.recall <= 100
  && typeof targets.p95 === 'number' && Number.isFinite(targets.p95) && targets.p95 > 0)
function submit() {
  validationError.value = ''
  if (!valid.value) { validationError.value = '请填写方案名称、算法参数、已入库模型（如适用）和三项验收目标。'; return }
  emit('save', { name: name.value.trim(), definition: algorithm.value === 'Classical'
    ? { algorithm: 'Classical', settings: { ...settings } }
    : { algorithm: 'PairedOnnx', modelSha256: modelSha256.value, thresholds: thresholds as RecipeScoreThresholds },
    targets: { minPrecision: targets.precision! / 100, minRecall: targets.recall! / 100, maxP95Ms: targets.p95! } })
}
</script>

<template>
  <form class="draft-editor" @submit.prevent="submit">
    <ElAlert v-if="error || validationError" :title="error || validationError" type="error" :closable="false" show-icon role="alert" />
    <div class="filter-field recipe-name-field"><label for="recipe-name">方案名称</label><ElInput id="recipe-name" v-model="name" maxlength="200" show-word-limit :disabled="saving" /></div>
    <div class="filter-field"><label for="recipe-algorithm">检测算法</label><ElSelect id="recipe-algorithm" v-model="algorithm" :disabled="saving"><ElOption :label="algorithmLabels.Classical" value="Classical" /><ElOption :label="algorithmLabels.PairedOnnx" value="PairedOnnx" /></ElSelect></div>
    <section v-if="algorithm === 'Classical'"><h3>经典定位 · Classical</h3><p class="section-note">参考图配准与差分定位，不输出六类标签。</p>
      <div class="recipe-form-grid"><div v-for="field in fields" :key="field.key" class="filter-field"><label :for="`setting-${field.key}`">{{ settingLabels[field.key] }}</label><ElInputNumber :id="`setting-${field.key}`" v-model="settings[field.key]" :min="field.min" :max="field.max" :step="field.step" :precision="field.precision" :disabled="saving" controls-position="right" /></div></div>
    </section>
    <section v-else><h3>成对 ONNX 六类检测</h3><p class="section-note">使用已入库、固定输入契约的正式导出模型；六个类别阈值必须逐一明确填写，不预设部署分数。</p>
      <ElAlert v-if="modelsError" :title="modelsError" type="error" :closable="false" show-icon><ElButton link @click="emit('refresh-models')">重试模型列表</ElButton></ElAlert>
      <div class="filter-field"><label for="recipe-model">模型 SHA256 / 大小</label><ElSelect id="recipe-model" v-model="modelSha256" filterable :loading="modelsLoading" :disabled="saving || !!modelsError || !models.length" placeholder="选择已入库 ONNX 模型"><ElOption v-for="model in models" :key="model.sha256" :label="`${model.sha256.slice(0, 16)}… · ${(model.byteLength / 1024 / 1024).toFixed(1)} MiB · ${model.inputContract}`" :value="model.sha256" /></ElSelect><p v-if="!modelsLoading && !models.length" class="section-note">尚无已入库模型，请先由工艺工程师在方案页上传。</p></div>
      <div class="recipe-form-grid"><div v-for="key in thresholdKeys" :key="key" class="filter-field"><label :for="`threshold-${key}`">{{ thresholdLabels[key] }} · 最低分数</label><ElInputNumber :id="`threshold-${key}`" v-model="thresholds[key]" :min="0" :max="1" :step="0.001" :precision="3" :disabled="saving" controls-position="right" /></div></div>
    </section>
    <section><h3>本草稿验收目标</h3><p class="section-note">目标随验证快照保存，用于本次方案判定；正式发布仍需独立冻结门槛。</p><div class="recipe-form-grid">
      <div class="filter-field"><label for="target-precision">最低精确率（%）</label><ElInputNumber id="target-precision" v-model="targets.precision" :min="0" :max="100" :step="0.01" :precision="2" :disabled="saving" controls-position="right" /></div>
      <div class="filter-field"><label for="target-recall">最低召回率（%）</label><ElInputNumber id="target-recall" v-model="targets.recall" :min="0" :max="100" :step="0.01" :precision="2" :disabled="saving" controls-position="right" /></div>
      <div class="filter-field"><label for="target-p95">最高 p95 耗时（ms）</label><ElInputNumber id="target-p95" v-model="targets.p95" :min="0.01" :step="0.01" :precision="2" :disabled="saving" controls-position="right" /></div>
    </div></section>
    <p class="section-note">保存后可在固定 200 张 validation 图像上执行真实检测；推理不读取标注。</p>
    <div class="editor-actions"><ElButton :disabled="saving" @click="emit('cancel')">取消</ElButton><ElButton type="primary" native-type="submit" :loading="saving">保存草稿</ElButton></div>
  </form>
</template>
