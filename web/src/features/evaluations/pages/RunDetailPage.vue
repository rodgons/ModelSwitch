<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { useMutation, useQueryClient } from '@tanstack/vue-query'
import { computed, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { useSession } from '@/app/session'
import Alert from '@/shared/components/Alert.vue'
import Badge from '@/shared/components/Badge.vue'
import Button from '@/shared/components/Button.vue'
import { link, table, typo } from '@/shared/components/components.styles'
import ConfirmDialog from '@/shared/components/ConfirmDialog.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import EstimatedBadge from '@/shared/components/EstimatedBadge.vue'
import Field from '@/shared/components/Field.vue'
import Identifier from '@/shared/components/Identifier.vue'
import Money from '@/shared/components/Money.vue'
import QueryView from '@/shared/components/QueryView.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import StatusBadge from '@/shared/components/StatusBadge.vue'
import {
  formatDuration,
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
import Split from '@/shared/layout/Split.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import Toolbar from '@/shared/layout/Toolbar.vue'
import { qualityMetricLabel, type RunAggregates } from '../aggregates'
import { activeStatuses } from '../comparison'
import { code } from '../evaluations.styles'
import { compactJson, prettyJson } from '../json'
import { firstPage, nextPage, previousPage } from '../pager'
import {
  cancelRun,
  exportUrl,
  recomputeAggregates,
  rerun,
  runKeys,
  useResults,
  useRun,
} from '../runQueries'

/** One Evaluation Run: its reproducibility record, aggregates, failures and Case Results (evaluation.md sections 5–7). */
const props = defineProps<{ runId: string }>()
const session = useSession()
const router = useRouter()
const queries = useQueryClient()
const run = useRun(() => props.runId)
const canWrite = computed(() => session.can('operator'))
const active = computed(() => activeStatuses.has(run.data.value?.status ?? ''))
const aggregates = computed(() => (run.data.value?.aggregates ?? null) as RunAggregates | null)

const status = ref('')
const page = ref(firstPage())
watch([status, () => props.runId], () => (page.value = firstPage()))
const results = useResults(
  () => props.runId,
  status,
  () => page.value.cursor,
)
// Fresh Case Results while the run is working.
watch(
  () => run.data.value?.progress.done,
  () => void results.refetch(),
)
const statusOptions = [
  { value: '', label: 'Any status' },
  { value: 'passed', label: 'Passed' },
  { value: 'failed', label: 'Failed' },
  { value: 'errored', label: 'Errored' },
  { value: 'skipped', label: 'Skipped' },
]

const refresh = () => queries.invalidateQueries({ queryKey: runKeys.all })
const cancelOpen = ref(false)
const cancel = useMutation({
  mutationFn: () => cancelRun(props.runId),
  onSuccess: async () => {
    cancelOpen.value = false
    await refresh()
  },
})
const again = useMutation({
  mutationFn: () => rerun(props.runId),
  onSuccess: async (created) => {
    await refresh()
    await router.push(`/comparisons/${created.comparisonId}`)
  },
})
const recompute = useMutation({
  mutationFn: () => recomputeAggregates(props.runId),
  onSuccess: refresh,
})

const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono, table.noWrap),
  inline: stylex.props(code.inline),
  block: stylex.props(code.block),
  caption: stylex.props(typo.caption),
  back: stylex.props(link.quiet, link.focus),
  link: stylex.props(link.inline, link.focus),
}
</script>

<template>
  <Container size="wide">
    <QueryView
      :data="run.data.value"
      :is-pending="run.isPending.value"
      :error="run.error.value"
      :refetch="run.refetch"
    >
      <template #default="{ data: detail }">
        <PageHeader :title="`Run: ${detail.reproducibility.target.label}`">
          <template #lede
            ><Inline :gap="8"
              ><StatusBadge :status="detail.status" /><span
                >{{ formatTokens(detail.progress.done) }} /
                {{ formatTokens(detail.progress.total) }} Cases</span
              ><code v-if="detail.error">{{ detail.error }}</code></Inline
            ></template
          >
          <template #actions>
            <Inline :gap="12">
              <RouterLink :to="`/comparisons/${detail.comparisonId}`" v-bind="s.back">{{
                detail.comparisonName
              }}</RouterLink>
              <a :href="exportUrl(detail.id, 'csv')" download v-bind="s.link">Export CSV</a>
              <a :href="exportUrl(detail.id, 'jsonl')" download v-bind="s.link">Export JSONL</a>
              <template v-if="canWrite">
                <Button v-if="active" destructive @click="cancelOpen = true">Cancel run</Button>
                <template v-else>
                  <Button :loading="recompute.isPending.value" @click="recompute.mutate()"
                    >Recompute aggregates</Button
                  >
                  <Button variant="primary" :loading="again.isPending.value" @click="again.mutate()"
                    >Run again</Button
                  >
                </template>
              </template>
            </Inline>
          </template>
        </PageHeader>
        <Stack :gap="40">
          <ErrorState v-if="again.error.value" :error="again.error.value" />
          <ErrorState v-if="recompute.error.value" :error="recompute.error.value" />
          <Alert v-if="recompute.isSuccess.value" tone="success"
            >Aggregates recomputed from the stored Case Results.</Alert
          >
          <Alert v-if="detail.aggregatesLive" tone="info"
            >The run is not finished: these aggregates come from the Case Results stored so far and
            update every two seconds.</Alert
          >
          <Split>
            <Stack :gap="40">
              <Section v-if="aggregates" title="Aggregates">
                <Facts
                  :items="[
                    {
                      label: qualityMetricLabel(aggregates.quality.metric),
                      value: formatScore(aggregates.quality.value),
                    },
                    { label: 'Errored', value: formatPercent(aggregates.erroredRate) },
                    { label: 'Passed', value: formatTokens(aggregates.passed) },
                    { label: 'Failed', value: formatTokens(aggregates.failed) },
                    { label: 'Errored Cases', value: formatTokens(aggregates.errored) },
                    { label: 'Skipped', value: formatTokens(aggregates.skipped) },
                    { label: 'p50 latency', value: formatDuration(aggregates.latency.p50Ms) },
                    { label: 'p95 latency', value: formatDuration(aggregates.latency.p95Ms) },
                    { label: 'Candidate cost', value: '', key: 'candidate' },
                    { label: 'Judge cost', value: '', key: 'judge' },
                    { label: 'Total cost', value: '', key: 'total' },
                    {
                      label: 'Candidate tokens',
                      value: `${formatTokens(aggregates.tokens.candidateInput)} in, ${formatTokens(aggregates.tokens.candidateOutput)} out`,
                    },
                  ]"
                >
                  <template #candidate
                    ><Money :amount="aggregates.cost.candidate" estimated
                  /></template>
                  <template #judge><Money :amount="aggregates.cost.judge" estimated /></template>
                  <template #total><Money :amount="aggregates.cost.total" estimated /></template>
                </Facts>
                <Badge v-if="aggregates.llmJudge" tone="accent">Model-judged</Badge>
              </Section>
              <Stack :gap="12">
                <Toolbar>
                  <Field v-slot="{ id }" label="Case status"
                    ><SelectInput :id="id" v-model="status" :options="statusOptions"
                  /></Field>
                </Toolbar>
                <Section title="Case Results" surface flush>
                  <QueryView
                    :data="results.data.value"
                    :is-pending="results.isPending.value"
                    :error="results.error.value"
                    :refetch="results.refetch"
                    :is-empty="(r) => r.items.length === 0"
                    empty-text="No Case Result matches yet."
                  >
                    <template #default="{ data: r }">
                      <TableScroll label="Case Results">
                        <table v-bind="s.table">
                          <thead>
                            <tr>
                              <th v-bind="s.thNumber" scope="col">#</th>
                              <th v-bind="s.th" scope="col">Case id</th>
                              <th v-bind="s.th" scope="col">Status</th>
                              <th v-bind="s.thNumber" scope="col">Score</th>
                              <th v-bind="s.th" scope="col">Predicted</th>
                              <th v-bind="s.thNumber" scope="col">Latency</th>
                              <th v-bind="s.thNumber" scope="col">
                                <Inline :gap="6" justify="end" :wrap="false" as="span"
                                  >Cost <EstimatedBadge
                                /></Inline>
                              </th>
                              <th v-bind="s.th" scope="col">Request</th>
                            </tr>
                          </thead>
                          <tbody>
                            <tr v-for="item in r.items" :key="item.id">
                              <td v-bind="s.number">{{ formatTokens(item.ordinal + 1) }}</td>
                              <td v-bind="s.td">
                                <RouterLink
                                  :to="{
                                    path: `/comparisons/${detail.comparisonId}`,
                                    query: { case: item.externalId },
                                  }"
                                  v-bind="s.link"
                                  >{{ item.externalId }}</RouterLink
                                >
                              </td>
                              <td v-bind="s.td">
                                <Stack :gap="2">
                                  <StatusBadge :status="item.status" />
                                  <code v-if="item.errorCode">{{ item.errorCode }}</code>
                                </Stack>
                              </td>
                              <td v-bind="s.number">{{ formatScore(item.score) }}</td>
                              <td v-bind="s.td">
                                <span v-bind="s.inline">{{
                                  compactJson(item.predicted, 40).text
                                }}</span>
                              </td>
                              <td v-bind="s.number">{{ formatDuration(item.latencyMs) }}</td>
                              <td v-bind="s.number">
                                <Money :amount="item.estimatedCost" estimated :badge="false" />
                              </td>
                              <td v-bind="s.td">
                                <RouterLink
                                  v-if="item.inferenceRequestId"
                                  :to="`/requests/${item.inferenceRequestId}`"
                                  v-bind="s.link"
                                  >Open</RouterLink
                                >
                              </td>
                            </tr>
                          </tbody>
                        </table>
                      </TableScroll>
                    </template>
                  </QueryView>
                </Section>
                <Inline justify="end" :gap="8">
                  <Button :disabled="!page.back.length" @click="page = previousPage(page)"
                    >Previous page</Button
                  >
                  <Button
                    :disabled="!results.data.value?.nextCursor"
                    @click="page = nextPage(page, results.data.value?.nextCursor ?? '')"
                    >Next page</Button
                  >
                </Inline>
              </Stack>
            </Stack>
            <template #aside>
              <Stack :gap="40">
                <Section title="Reproducibility">
                  <Facts
                    :items="[
                      { label: 'Target', value: detail.reproducibility.target.label },
                      { label: 'Dataset Version', value: '', key: 'dataset' },
                      { label: 'Content SHA-256', value: '', key: 'content' },
                      { label: 'Prompt Version', value: '', key: 'prompt' },
                      {
                        label: 'Evaluator',
                        value: `${detail.reproducibility.evaluator.kind} v${detail.reproducibility.evaluator.version}`,
                      },
                      { label: 'Config SHA-256', value: '', key: 'config' },
                      { label: 'Gateway version', value: detail.reproducibility.gatewayVersion },
                      { label: 'Started', value: formatTimestamp(detail.startedAt) },
                      { label: 'Completed', value: formatTimestamp(detail.completedAt) },
                      { label: 'Cost cap', value: '', key: 'cap' },
                    ]"
                  >
                    <template #dataset>
                      <RouterLink
                        :to="`/evaluations/datasets/${detail.reproducibility.datasetId}/versions/${detail.reproducibility.datasetVersion}`"
                        v-bind="s.link"
                        >v{{ detail.reproducibility.datasetVersion }}</RouterLink
                      >
                    </template>
                    <template #content
                      ><Identifier :value="detail.reproducibility.contentSha256" copy
                    /></template>
                    <template #prompt>
                      <RouterLink
                        :to="{
                          path: `/evaluations/prompts/${detail.reproducibility.prompt.id}`,
                          query: { version: String(detail.reproducibility.prompt.version) },
                        }"
                        v-bind="s.link"
                        >v{{ detail.reproducibility.prompt.version }}</RouterLink
                      >
                    </template>
                    <template #config><Identifier :value="detail.configSha256" copy /></template>
                    <template #cap>
                      <Money
                        v-if="detail.maxEstimatedCost !== null"
                        :amount="detail.maxEstimatedCost"
                        estimated
                      />
                      <span v-else>None</span>
                    </template>
                  </Facts>
                </Section>
                <Section title="Failures">
                  <Stack :gap="8">
                    <Facts
                      :items="
                        Object.entries(detail.failures.byStatus).map(([k, v]) => ({
                          label: k,
                          value: formatTokens(v),
                        }))
                      "
                    />
                    <Facts
                      v-if="Object.keys(detail.failures.byErrorCode).length"
                      :items="
                        Object.entries(detail.failures.byErrorCode).map(([k, v]) => ({
                          label: k,
                          value: formatTokens(v),
                        }))
                      "
                    />
                    <span v-else v-bind="s.caption">No error code recorded.</span>
                  </Stack>
                </Section>
                <Section title="Run Configuration">
                  <pre
                    v-bind="s.block"
                    role="region"
                    tabindex="0"
                    aria-label="Frozen Run Configuration"
                    >{{ prettyJson(detail.configuration) }}</pre>
                </Section>
              </Stack>
            </template>
          </Split>
        </Stack>
        <ConfirmDialog
          v-model:visible="cancelOpen"
          :title="`Cancel run ${detail.reproducibility.target.label}?`"
          action="Cancel run"
          :pending="cancel.isPending.value"
          @confirm="cancel.mutate()"
        >
          <p>Cases in flight finish; no new Case starts. The run ends cancelled.</p>
          <ErrorState v-if="cancel.error.value" :error="cancel.error.value" />
        </ConfirmDialog>
      </template>
    </QueryView>
  </Container>
</template>
