<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElAlert, ElOption, ElPagination, ElSelect, ElTable, ElTableColumn, ElTag } from 'element-plus'
import { decisionLabels, formatMs, formatTime } from '../inspections'
import { formatPercent, settingLabels } from '../recipes'
import type { ValidationRun } from '../recipes'

const props = defineProps<{ run: ValidationRun; currentSnapshotHash: string }>()
const filter = ref('all')
const page = ref(1)
const report = computed(() => props.run.report)
const targets = computed(() => props.run.snapshot.targets)
const rows = computed(() => (report.value?.rows ?? []).filter(row => {
  if (filter.value === 'failed') return row.status === 'Failed'
  if (filter.value === 'missed') return row.fn > 0
  if (filter.value === 'false-positive') return row.fp > 0
  if (filter.value === 'issues') return row.status === 'Failed' || row.fn > 0 || row.fp > 0
  return true
}))
const visibleRows = computed(() => rows.value.slice((page.value - 1) * 20, page.value * 20))
watch(filter, () => { page.value = 1 })
watch(() => props.run.id, () => { filter.value = 'all'; page.value = 1 })
</script>

<template>
  <section v-if="report" class="recipe-report">
    <ElAlert v-if="run.snapshot.snapshotHash !== currentSnapshotHash" title="这是历史快照的报告，当前草稿已修改。下方参数、目标和结论均对应本次运行，不能用于判定修改后的草稿。" type="warning" :closable="false" show-icon />
    <div class="report-heading">
      <div><h3>验证报告</h3><p class="section-note">{{ formatTime(run.completedAt) }} 完成 · {{ report.rows.length }} / {{ run.total }} 张 · {{ run.snapshot.name }}</p></div>
      <ElTag :type="report.meetsTargets ? 'success' : 'danger'" size="large">{{ report.meetsTargets ? '达到本次快照目标' : '未达到本次快照目标' }}</ElTag>
    </div>
    <div class="report-metrics">
      <div><span>精确率 Precision</span><strong>{{ formatPercent(report.precision) }}</strong><small :class="report.precision >= targets.minPrecision ? 'metric-pass' : 'metric-fail'">目标 ≥ {{ formatPercent(targets.minPrecision) }} · {{ report.precision >= targets.minPrecision ? '达到' : '未达' }}</small></div>
      <div><span>召回率 Recall</span><strong>{{ formatPercent(report.recall) }}</strong><small :class="report.recall >= targets.minRecall ? 'metric-pass' : 'metric-fail'">目标 ≥ {{ formatPercent(targets.minRecall) }} · {{ report.recall >= targets.minRecall ? '达到' : '未达' }}</small></div>
      <div><span>F1</span><strong>{{ formatPercent(report.f1) }}</strong><small>定位精确率与召回率的调和平均</small></div>
      <div><span>p95 检测耗时</span><strong>{{ formatMs(report.p95Ms) }}</strong><small :class="report.p95Ms <= targets.maxP95Ms ? 'metric-pass' : 'metric-fail'">目标 ≤ {{ formatMs(targets.maxP95Ms) }} · {{ report.p95Ms <= targets.maxP95Ms ? '达到' : '未达' }}</small></div>
    </div>
    <dl class="report-counts">
      <div><dt>匹配 TP</dt><dd>{{ report.tp }}</dd></div><div><dt>误检 FP</dt><dd>{{ report.fp }}</dd></div><div><dt>漏检 FN</dt><dd>{{ report.fn }}</dd></div>
      <div><dt>执行失败</dt><dd :class="report.executionFailures ? 'metric-fail' : ''">{{ report.executionFailures }} / {{ run.total }}</dd></div>
      <div><dt>p50 检测耗时</dt><dd>{{ formatMs(report.p50Ms) }}</dd></div><div><dt>首张冷启动</dt><dd>{{ formatMs(report.coldSampleMs) }}</dd></div>
    </dl>
    <p class="section-note">{{ report.timingDescription }}</p>
    <p class="section-note">经典基线按不区分类别的定位匹配计算，IoU ≥ 0.5。三项目标均达到且执行失败为 0 才算达标。</p>
    <details class="recipe-snapshot">
      <summary>查看本次运行的参数与证据标识</summary>
      <dl class="recipe-settings"><div v-for="(value, key) in run.snapshot.settings" :key="key"><dt>{{ settingLabels[key] }}</dt><dd>{{ value }}</dd></div></dl>
      <dl class="snapshot-identifiers">
        <div><dt>验证编号</dt><dd>{{ run.id }}</dd></div><div><dt>参数与目标快照</dt><dd>{{ run.snapshot.snapshotHash }}</dd></div>
        <div><dt>固定输入清单 SHA256</dt><dd>{{ run.snapshot.dataManifestSha256 }}</dd></div><div><dt>算法程序集 SHA256</dt><dd>{{ report.algorithmAssemblySha256 }}</dd></div>
        <div><dt>执行环境</dt><dd>{{ report.runtime }} · {{ report.machine }}</dd></div>
      </dl>
    </details>
    <div class="report-table-heading">
      <h3>逐样本报告 <span class="record-count">{{ rows.length }}</span></h3>
      <div class="sample-filter"><label for="sample-filter">筛选样本</label><ElSelect id="sample-filter" v-model="filter" aria-label="筛选样本">
        <ElOption label="全部样本" value="all" /><ElOption label="有漏检、误检或执行失败" value="issues" /><ElOption label="仅执行失败" value="failed" /><ElOption label="有漏检 FN" value="missed" /><ElOption label="有误检 FP" value="false-positive" />
      </ElSelect></div>
    </div>
    <ElTable :data="visibleRows" row-key="sampleId" class="inspection-table report-table" empty-text="没有符合筛选条件的样本">
      <ElTableColumn prop="sampleId" label="样本" min-width="110" />
      <ElTableColumn label="执行 / 判定" min-width="140"><template #default="{ row }"><ElTag :type="row.status === 'Failed' ? 'danger' : 'info'">{{ row.status === 'Failed' ? '执行失败' : decisionLabels[row.decision as keyof typeof decisionLabels] }}</ElTag></template></ElTableColumn>
      <ElTableColumn prop="tp" label="TP" width="65" /><ElTableColumn prop="fp" label="FP" width="65" /><ElTableColumn prop="fn" label="FN" width="65" />
      <ElTableColumn label="检出区域" width="90"><template #default="{ row }">{{ row.defects.length }}</template></ElTableColumn>
      <ElTableColumn label="耗时" min-width="105"><template #default="{ row }">{{ formatMs(row.elapsedMs) }}</template></ElTableColumn>
      <ElTableColumn prop="error" label="执行错误" min-width="180"><template #default="{ row }">{{ row.error || '—' }}</template></ElTableColumn>
    </ElTable>
    <div class="pagination-row"><span class="pagination-caption">显示筛选后的 {{ rows.length }} 张样本</span><ElPagination v-model:current-page="page" :page-size="20" :total="rows.length" layout="prev, pager, next" /></div>
  </section>
</template>
