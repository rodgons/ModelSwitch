<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { useMutation, useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import type { Schemas } from '@/api/client'
import { useSession } from '@/app/session'
import Alert from '@/shared/components/Alert.vue'
import Badge from '@/shared/components/Badge.vue'
import Button from '@/shared/components/Button.vue'
import { link, table, typo } from '@/shared/components/components.styles'
import ConfirmDialog from '@/shared/components/ConfirmDialog.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import EstimatedBadge from '@/shared/components/EstimatedBadge.vue'
import Money from '@/shared/components/Money.vue'
import QueryView from '@/shared/components/QueryView.vue'
import ScatterChart from '@/shared/components/ScatterChart.vue'
import StatusBadge from '@/shared/components/StatusBadge.vue'
import {
  formatDuration,
  formatMoney,
  formatPercent,
  formatScore,
  formatTimestamp,
  formatTokens,
} from '@/shared/format'
import Container from '@/shared/layout/Container.vue'
import Facts from '@/shared/layout/Facts.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { qualityMetricLabel, type RunAggregates } from '../aggregates'
import CaseDrillDown from '../components/CaseDrillDown.vue'
import DeleteAction from '../components/DeleteAction.vue'
import RunMetrics from '../components/RunMetrics.vue'
import { activeStatuses, costAxisMax, describeVariance, scatterPoints } from '../comparison'
import { useDataset } from '../queries'
import {
  cancelRun,
  deleteComparison,
  exportUrl,
  rerun,
  runKeys,
  useComparison,
  useRuns,
} from '../runQueries'

/** Runs of one Comparison side by side (frontend.md section 2), polled while any is queued or running. */
const props = defineProps<{ id: string }>()
const session = useSession()
const route = useRoute()
const router = useRouter()
const queries = useQueryClient()
const comparison = useComparison(() => props.id)
const dataset = useDataset(() => comparison.data.value?.datasetId ?? '')
const canWrite = computed(() => session.can('operator'))

type Run = Schemas['ComparisonRunView']
const runs = computed<Run[]>(() => comparison.data.value?.runs ?? [])
/** Runs are numbered in configuration order, so two runs on the same target stay distinguishable. */
const labelOf = (run: Run, index: number) => `${index + 1}. ${run.target.label}`
const labelled = computed(() =>
  runs.value.map((r, i) => ({ runId: r.runId, label: labelOf(r, i) })),
)
const details = useRuns(() => runs.value.map((r) => r.runId))
const metrics = computed(() =>
  runs.value.map((r, i) => ({
    label: labelOf(r, i),
    aggregates: (details.value[i]?.data?.aggregates ?? null) as RunAggregates | null,
  })),
)
const points = computed(() =>
  scatterPoints(runs.value.map((r, i) => ({ ...r, target: { label: labelOf(r, i) } }))).map(
    (p) => ({ label: p.label, x: p.cost, y: p.quality }),
  ),
)
const xMax = computed(() => costAxisMax(points.value.map((p) => p.x)))
const anyLive = computed(() => runs.value.some((r) => activeStatuses.has(r.status)))
const anyVariance = computed(() => runs.value.some((r) => describeVariance(r.variance)))
const initialCase = typeof route.query.case === 'string' ? route.query.case : undefined

const cancelling = ref<Run | null>(null)
const cancelOpen = computed({
  get: () => cancelling.value !== null,
  set: (open) => !open && (cancelling.value = null),
})
const cancel = useMutation({
  mutationFn: (runId: string) => cancelRun(runId),
  onSuccess: async () => {
    cancelling.value = null
    await queries.invalidateQueries({ queryKey: runKeys.all })
  },
})
const again = useMutation({
  mutationFn: (runId: string) => rerun(runId),
  onSuccess: async (created) => {
    await queries.invalidateQueries({ queryKey: runKeys.all })
    await router.push(`/comparisons/${created.comparisonId}`)
  },
})

const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono, table.noWrap),
  caption: stylex.props(typo.caption),
  back: stylex.props(link.quiet, link.focus),
  link: stylex.props(link.inline, link.focus),
}
</script>

<template>
  <Container size="wide">
    <QueryView
      :data="comparison.data.value"
      :is-pending="comparison.isPending.value"
      :error="comparison.error.value"
      :refetch="comparison.refetch"
    >
      <template #default="{ data: detail }">
        <PageHeader
          :title="detail.name"
          lede="Comparison"
          :back="{ to: '/evaluations?tab=comparisons', label: 'All Comparisons' }"
        >
          <template #actions>
            <Inline :gap="12">
              <Button
                v-if="canWrite"
                :to="{
                  path: '/evaluations/new',
                  query: {
                    tenantId: detail.tenantId,
                    datasetId: detail.datasetId,
                    version: String(detail.datasetVersion),
                  },
                }"
                >Start a run on this Dataset</Button
              >
              <DeleteAction
                v-if="canWrite"
                kind="Comparison"
                :name="detail.name"
                :remove="() => deleteComparison(detail.id)"
                :invalidate="runKeys.all"
                to="/evaluations?tab=comparisons"
              >
                <p>
                  Its {{ detail.runs.length }} run(s) and every Case Result, model outputs included,
                  are deleted. A run that is queued or running must be cancelled and stopped first.
                </p>
              </DeleteAction>
            </Inline>
          </template>
        </PageHeader>
        <Stack :gap="40">
          <Facts
            :items="[
              { label: 'Dataset', value: '', key: 'dataset' },
              { label: 'Runs', value: formatTokens(detail.runs.length) },
              { label: 'Created', value: formatTimestamp(detail.createdAt) },
            ]"
          >
            <template #dataset>
              <RouterLink
                :to="`/evaluations/datasets/${detail.datasetId}/versions/${detail.datasetVersion}`"
                v-bind="s.link"
                >{{ dataset.data.value?.name ?? 'Dataset' }} v{{
                  detail.datasetVersion
                }}</RouterLink
              >
            </template>
          </Facts>
          <Alert v-if="anyLive" tone="info"
            >Runs in progress: figures update every two seconds and are computed from the Case
            Results stored so far.</Alert
          >
          <Section title="Side by side" surface flush>
            <TableScroll label="Runs side by side">
              <table v-bind="s.table">
                <thead>
                  <tr>
                    <th v-bind="s.th" scope="col">Configuration</th>
                    <th v-bind="s.th" scope="col">Status</th>
                    <th v-bind="s.thNumber" scope="col">Quality</th>
                    <th v-bind="s.thNumber" scope="col">Errored</th>
                    <th v-bind="s.thNumber" scope="col">p50</th>
                    <th v-bind="s.thNumber" scope="col">p95</th>
                    <th v-bind="s.thNumber" scope="col">
                      <Inline :gap="6" justify="end" :wrap="false" as="span"
                        >Cost per Case <EstimatedBadge
                      /></Inline>
                    </th>
                    <th v-bind="s.thNumber" scope="col">
                      <Inline :gap="6" justify="end" :wrap="false" as="span"
                        >Candidate cost <EstimatedBadge
                      /></Inline>
                    </th>
                    <th v-bind="s.thNumber" scope="col">
                      <Inline :gap="6" justify="end" :wrap="false" as="span"
                        >Judge cost <EstimatedBadge
                      /></Inline>
                    </th>
                    <th v-bind="s.thNumber" scope="col">
                      <Inline :gap="6" justify="end" :wrap="false" as="span"
                        >Total <EstimatedBadge
                      /></Inline>
                    </th>
                    <th v-bind="s.th" scope="col">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="(run, index) in detail.runs" :key="run.runId">
                    <td v-bind="s.td">
                      <Stack :gap="2">
                        <span v-bind="s.mono">{{ labelOf(run, index) }}</span>
                        <span v-bind="s.caption"
                          >Prompt v{{ run.prompt.version }}, {{ run.evaluator.kind }} v{{
                            run.evaluator.version
                          }}</span
                        >
                        <span
                          ><Badge v-if="run.modelJudged" tone="accent">Model-judged</Badge></span
                        >
                      </Stack>
                    </td>
                    <td v-bind="s.td">
                      <Stack :gap="2">
                        <StatusBadge :status="run.status" />
                        <span v-bind="s.caption"
                          >{{ formatTokens(run.casesDone) }} /
                          {{ formatTokens(run.casesTotal) }} Cases</span
                        >
                        <code v-if="run.error">{{ run.error }}</code>
                      </Stack>
                    </td>
                    <td v-bind="s.number">
                      <Stack :gap="2">
                        <span>{{ formatScore(run.quality?.value) }}</span>
                        <span v-if="run.quality" v-bind="s.caption">{{
                          qualityMetricLabel(run.quality.metric)
                        }}</span>
                        <span v-if="describeVariance(run.variance)" v-bind="s.caption">{{
                          describeVariance(run.variance)
                        }}</span>
                      </Stack>
                    </td>
                    <td v-bind="s.number">{{ formatPercent(run.erroredRate) }}</td>
                    <td v-bind="s.number">{{ formatDuration(run.latencyP50Ms) }}</td>
                    <td v-bind="s.number">{{ formatDuration(run.latencyP95Ms) }}</td>
                    <td v-bind="s.number">
                      <Money :amount="run.candidateCostPerCase" estimated :badge="false" />
                    </td>
                    <td v-bind="s.number">
                      <Money :amount="run.candidateCost" estimated :badge="false" />
                    </td>
                    <td v-bind="s.number">
                      <Money :amount="run.judgeCost" estimated :badge="false" />
                    </td>
                    <td v-bind="s.number">
                      <Money :amount="run.totalCost" estimated :badge="false" />
                    </td>
                    <td v-bind="s.td">
                      <Inline :gap="8" :wrap="false">
                        <RouterLink :to="`/evaluations/runs/${run.runId}`" v-bind="s.link"
                          >Details</RouterLink
                        >
                        <a :href="exportUrl(run.runId, 'csv')" download v-bind="s.link">CSV</a>
                        <a :href="exportUrl(run.runId, 'jsonl')" download v-bind="s.link">JSONL</a>
                        <Button
                          v-if="canWrite && activeStatuses.has(run.status)"
                          size="sm"
                          destructive
                          @click="cancelling = run"
                          >Cancel</Button
                        >
                        <Button
                          v-if="canWrite && !activeStatuses.has(run.status)"
                          size="sm"
                          :loading="again.isPending.value && again.variables.value === run.runId"
                          @click="again.mutate(run.runId)"
                          >Run again</Button
                        >
                      </Inline>
                    </td>
                  </tr>
                </tbody>
              </table>
            </TableScroll>
          </Section>
          <ErrorState v-if="again.error.value" :error="again.error.value" />
          <Alert tone="info"
            >LLM outputs are not deterministic, even at temperature 0: the same configuration can
            score differently from run to run.
            {{
              anyVariance
                ? 'The quality column shows the run-to-run spread where a configuration has run more than once.'
                : 'Use Run again on a configuration to measure its run-to-run spread.'
            }}</Alert
          >
          <Section title="Quality and per-Case candidate cost" surface>
            <ScatterChart
              v-if="points.length"
              label="Quality against Estimated Cost per Case, one point per run"
              :points="points"
              x-name="Estimated Cost per Case (candidate)"
              y-name="Quality"
              :format-x="formatMoney"
              :format-y="formatScore"
              :x-max="xMax"
              :y-max="1"
            />
            <span v-else v-bind="s.caption">No run has both a quality and a cost yet.</span>
          </Section>
          <RunMetrics :runs="metrics" />
          <CaseDrillDown :runs="labelled" :initial-case="initialCase" />
        </Stack>
        <ConfirmDialog
          v-model:visible="cancelOpen"
          :title="`Cancel run ${cancelling?.target.label ?? ''}?`"
          action="Cancel run"
          :pending="cancel.isPending.value"
          @confirm="cancelling && cancel.mutate(cancelling.runId)"
        >
          <p>Cases in flight finish; no new Case starts. The run ends cancelled.</p>
          <ErrorState v-if="cancel.error.value" :error="cancel.error.value" />
        </ConfirmDialog>
      </template>
    </QueryView>
  </Container>
</template>
