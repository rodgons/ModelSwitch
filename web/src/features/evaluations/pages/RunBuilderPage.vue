<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { useMutation, useQueryClient } from '@tanstack/vue-query'
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError, type Schemas } from '@/api/client'
import { useSession } from '@/app/session'
import { useModelNames } from '@/features/providers/queries'
import { usePolicies } from '@/features/routing/queries'
import { useTenants } from '@/features/tenants/queries'
import Alert from '@/shared/components/Alert.vue'
import Badge from '@/shared/components/Badge.vue'
import Button from '@/shared/components/Button.vue'
import { table, typo } from '@/shared/components/components.styles'
import ErrorState from '@/shared/components/ErrorState.vue'
import EstimatedBadge from '@/shared/components/EstimatedBadge.vue'
import Field from '@/shared/components/Field.vue'
import LoadingState from '@/shared/components/LoadingState.vue'
import Money from '@/shared/components/Money.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { formatTokens } from '@/shared/format'
import Container from '@/shared/layout/Container.vue'
import Grid from '@/shared/layout/Grid.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Split from '@/shared/layout/Split.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import ConfigurationEditor from '../components/ConfigurationEditor.vue'
import { taskKindLabel } from '../labels'
import {
  useDataset,
  useDatasets,
  useDatasetVersion,
  useDatasetVersions,
  usePrompts,
} from '../queries'
import {
  applySchema,
  type ConfigForm,
  estimateBody,
  evaluatorKindsFor,
  groupRunErrors,
  maxConfigurations,
  newConfiguration,
  type RunForm,
  type ScoringSchema,
  toRequest,
} from '../runForm'
import { createRun, estimateRun, runKeys } from '../runQueries'

/**
 * The run builder (frontend.md section 2): one Dataset Version against 1–6 Run Configurations, with the worst-case Estimated Cost
 * shown before anything starts (evaluation.md section 4, "Spend").
 */
const session = useSession()
const route = useRoute()
const router = useRouter()
const queries = useQueryClient()
const canWrite = computed(() => session.can('operator'))
const param = (key: string) => (typeof route.query[key] === 'string' ? route.query[key] : '')

const form = reactive<RunForm>({
  tenantId: param('tenantId'),
  datasetId: param('datasetId'),
  datasetVersion: param('version'),
  name: '',
  concurrency: '',
  maxEstimatedCost: '',
  configurations: [],
})

const tenants = useTenants()
const datasets = useDatasets(
  () => form.tenantId,
  () => null,
)
const prompts = usePrompts(
  () => form.tenantId,
  () => null,
)
const versions = useDatasetVersions(() => form.datasetId)
const version = useDatasetVersion(
  () => form.datasetId,
  () => Number(form.datasetVersion) || 0,
)
const policies = usePolicies()
const modelNames = useModelNames()

const tenantOptions = computed(() => [
  { value: '', label: 'Choose a Tenant' },
  ...(tenants.data.value?.items ?? []).map((t) => ({ value: t.id, label: t.name })),
])
const datasetDetail = useDataset(() => form.datasetId)
const dataset = computed(() =>
  datasetDetail.data.value?.id === form.datasetId ? datasetDetail.data.value : undefined,
)
const taskKind = computed(() => dataset.value?.taskKind ?? '')
const datasetOptions = computed(() => [
  { value: '', label: form.tenantId ? 'Choose a Dataset' : 'Choose a Tenant first' },
  ...(datasets.data.value?.items ?? [])
    .filter((d) => d.latestVersion > 0)
    .map((d) => ({ value: d.id, label: `${d.name} (${taskKindLabel(d.taskKind)})` })),
])
const versionOptions = computed(() => [
  { value: '', label: 'Choose a version' },
  ...(versions.data.value?.items ?? []).map((v) => ({
    value: String(v.version),
    label: `v${v.version} (${formatTokens(v.caseCount)} Cases)`,
  })),
])
const promptOptions = computed(() =>
  (prompts.data.value?.items ?? [])
    .filter((p) => p.latestVersion > 0)
    .map((p) => ({ value: p.id, label: p.name })),
)
const aliasOptions = computed(() =>
  [...new Set((policies.data.value?.items ?? []).flatMap((p) => p.aliases))]
    .sort()
    .map((a) => ({ value: a, label: a })),
)
const modelOptions = computed(() =>
  [...modelNames.value.entries()]
    .sort(([, a], [, b]) => a.localeCompare(b))
    .map(([id, name]) => ({ value: id, label: name })),
)

const schema = computed(() => (version.data.value?.scoringSchema ?? null) as ScoringSchema)

// Changing the Tenant clears what belongs to it; a new Dataset starts on its latest version; a new version refills labels and fields.
watch(
  () => form.tenantId,
  (tenantId) => {
    if (dataset.value && dataset.value.tenantId !== tenantId) form.datasetId = ''
    form.configurations.forEach((c) => (c.promptId = ''))
  },
)
// Opened from a Dataset (`?datasetId=`) without a Tenant: take the Dataset's.
watch(dataset, (value) => {
  if (value && !form.tenantId) form.tenantId = value.tenantId
})
watch(
  () => versions.data.value,
  (page) => {
    if (page && !page.items.some((v) => String(v.version) === form.datasetVersion))
      form.datasetVersion = page.items[0] ? String(page.items[0].version) : ''
  },
)
watch(taskKind, (kind) => {
  if (!kind) return
  if (!form.configurations.length) form.configurations.push(newConfiguration(kind, schema.value))
  const kinds = evaluatorKindsFor(kind)
  form.configurations.forEach((c) => {
    if (!kinds.includes(c.evaluatorKind)) c.evaluatorKind = kinds[0]!
  })
})
watch(schema, (value) => {
  form.configurations.forEach((c, i) => (form.configurations[i] = applySchema(c, value)))
})

function addConfiguration() {
  form.configurations.push(newConfiguration(taskKind.value, schema.value))
}
function duplicate(index: number) {
  const copy = JSON.parse(JSON.stringify(form.configurations[index])) as ConfigForm
  form.configurations.splice(index + 1, 0, copy)
}
function remove(index: number) {
  form.configurations.splice(index, 1)
}

// The worst-case estimate: recomputed 600 ms after the last change, once every reference is chosen. Late answers are dropped.
const estimate = ref<Schemas['EvaluationEstimateView'] | null>(null)
const estimateError = ref<unknown>(null)
const estimating = ref(false)
const serverErrors = ref<Record<string, string[]>>({})
let sequence = 0
let timer: ReturnType<typeof setTimeout> | undefined
const body = computed(() => estimateBody(form))
watch(
  () => JSON.stringify(body.value),
  () => {
    clearTimeout(timer)
    const current = body.value
    if (!current || !canWrite.value) {
      estimate.value = null
      estimateError.value = null
      return
    }
    timer = setTimeout(async () => {
      const mine = ++sequence
      estimating.value = true
      try {
        const result = await estimateRun(current)
        if (mine !== sequence) return
        estimate.value = result
        estimateError.value = null
        serverErrors.value = {}
      } catch (error) {
        if (mine !== sequence) return
        estimate.value = null
        estimateError.value = error
        serverErrors.value = error instanceof ApiError ? error.fieldErrors : {}
      } finally {
        if (mine === sequence) estimating.value = false
      }
    }, 600)
  },
  { immediate: true },
)
onBeforeUnmount(() => clearTimeout(timer))

const clientErrors = ref<Record<string, string[]>>({})
const create = useMutation({
  mutationFn: createRun,
  onSuccess: async (created) => {
    await queries.invalidateQueries({ queryKey: runKeys.all })
    await router.push(`/comparisons/${created.comparisonId}`)
  },
  onError: (error) => {
    serverErrors.value = error instanceof ApiError ? error.fieldErrors : {}
  },
})
function start() {
  const result = toRequest(form)
  if (!result.ok) {
    clientErrors.value = result.errors
    return
  }
  clientErrors.value = {}
  create.mutate(result.body)
}
watch(
  () => JSON.stringify(form),
  () => (clientErrors.value = {}),
)

const grouped = computed(() => groupRunErrors({ ...serverErrors.value, ...clientErrors.value }))
const top = (key: string) => grouped.value.top[key]
const placedTop = [
  'tenantId',
  'datasetId',
  'datasetVersion',
  'name',
  'concurrency',
  'maxEstimatedCost',
]
const generalErrors = computed(() =>
  Object.entries(grouped.value.top)
    .filter(([key]) => !placedTop.includes(key))
    .flatMap(([key, messages]) =>
      key === 'configurations' ? messages : messages.map((m) => `${key}: ${m}`),
    ),
)
const createFailure = computed(() =>
  create.error.value &&
  !(create.error.value instanceof ApiError && create.error.value.status === 400)
    ? create.error.value
    : null,
)
const estimateFailure = computed(() =>
  estimateError.value &&
  !(estimateError.value instanceof ApiError && estimateError.value.status === 400)
    ? estimateError.value
    : null,
)
const anyExceeds = computed(() =>
  estimate.value?.configurations.some((c) => c.exceedsMaxEstimatedCost),
)

const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  caption: stylex.props(typo.caption),
  mono: stylex.props(typo.mono, table.noWrap),
}
</script>

<template>
  <Container size="wide">
    <PageHeader
      title="Start a run"
      lede="Run one Dataset Version against up to six configurations side by side. Each configuration becomes one Evaluation Run of a new Comparison."
    />
    <Alert v-if="!canWrite" tone="info">Starting runs needs the Operator role.</Alert>
    <form v-else novalidate @submit.prevent="start">
      <Split>
        <Stack :gap="24">
          <Section title="Dataset and run" surface>
            <Stack :gap="12">
              <Alert v-if="generalErrors.length" tone="danger"
                >The run was rejected (<code>validation_failed</code>).
                {{ generalErrors.join(' ') }}</Alert
              >
              <Grid min="field" :gap="12">
                <Field v-slot="{ id }" label="Tenant" :errors="top('tenantId')"
                  ><SelectInput :id="id" v-model="form.tenantId" :options="tenantOptions"
                /></Field>
                <Field v-slot="{ id }" label="Dataset" :errors="top('datasetId')"
                  ><SelectInput :id="id" v-model="form.datasetId" :options="datasetOptions"
                /></Field>
                <Field v-slot="{ id }" label="Dataset Version" :errors="top('datasetVersion')"
                  ><SelectInput :id="id" v-model="form.datasetVersion" :options="versionOptions"
                /></Field>
                <Field v-slot="{ id, invalid }" label="Comparison name" :errors="top('name')"
                  ><TextInput :id="id" v-model="form.name" :invalid="invalid"
                /></Field>
                <Field
                  v-slot="{ id, invalid }"
                  label="Concurrency"
                  help="Cases in flight per run, 1–16. Empty: 4."
                  :errors="top('concurrency')"
                  ><TextInput
                    :id="id"
                    v-model="form.concurrency"
                    inputmode="numeric"
                    :invalid="invalid"
                /></Field>
                <Field
                  v-slot="{ id, invalid }"
                  label="Cost cap per run (USD)"
                  help="Max Estimated Cost of each run. Once reached, the remaining Cases are skipped. Empty: none."
                  :errors="top('maxEstimatedCost')"
                  ><TextInput
                    :id="id"
                    v-model="form.maxEstimatedCost"
                    inputmode="decimal"
                    :invalid="invalid"
                /></Field>
              </Grid>
            </Stack>
          </Section>
          <template v-if="taskKind">
            <ConfigurationEditor
              v-for="(_, index) in form.configurations"
              :key="index"
              v-model:config="form.configurations[index]!"
              :index="index"
              :task-kind="taskKind"
              :prompts="promptOptions"
              :aliases="aliasOptions"
              :models="modelOptions"
              :errors="grouped.configuration(index)"
              :can-remove="form.configurations.length > 1"
              :can-duplicate="form.configurations.length < maxConfigurations"
              @remove="remove(index)"
              @duplicate="duplicate(index)"
            />
            <div>
              <Button
                :disabled="form.configurations.length >= maxConfigurations"
                @click="addConfiguration"
                >Add configuration</Button
              >
            </div>
          </template>
          <Alert v-else tone="info"
            >Choose a Tenant and a Dataset with at least one version to configure the runs.</Alert
          >
        </Stack>
        <template #aside>
          <Stack :gap="24">
            <Section title="Worst-case Estimated Cost" surface>
              <Stack :gap="12">
                <span v-bind="s.caption"
                  >Each Case counted at the Budget Reservation its calls would hold, plus the
                  judge's for llm_judge. The actual Estimated Cost is usually far lower.</span
                >
                <span v-if="!body" v-bind="s.caption"
                  >Choose a Dataset Version and, for each configuration, a target and a Prompt
                  Version to see the estimate.</span
                >
                <LoadingState v-else-if="estimating && !estimate" />
                <ErrorState v-else-if="estimateFailure" :error="estimateFailure" />
                <Alert v-else-if="estimateError" tone="warning"
                  >Fix the highlighted fields to see the estimate.</Alert
                >
                <template v-else-if="estimate">
                  <Inline :gap="8" align="baseline">
                    <span v-bind="s.caption">Total</span>
                    <Money :amount="estimate.total" estimated />
                  </Inline>
                  <TableScroll label="Estimate per configuration">
                    <table v-bind="s.table">
                      <thead>
                        <tr>
                          <th v-bind="s.th" scope="col">#</th>
                          <th v-bind="s.thNumber" scope="col">Cases</th>
                          <th v-bind="s.thNumber" scope="col">
                            <Inline :gap="6" justify="end" :wrap="false" as="span"
                              >Worst case <EstimatedBadge
                            /></Inline>
                          </th>
                        </tr>
                      </thead>
                      <tbody>
                        <tr v-for="row in estimate.configurations" :key="row.index">
                          <td v-bind="s.td">
                            <Stack :gap="2">
                              <span v-bind="s.mono">{{ row.index + 1 }}. {{ row.target }}</span>
                              <span v-if="row.judgeWorstCase > 0" v-bind="s.caption"
                                >Judge <Money :amount="row.judgeWorstCase" estimated :badge="false"
                              /></span>
                              <Badge v-if="row.exceedsMaxEstimatedCost" tone="warning"
                                >Over the cost cap</Badge
                              >
                              <span
                                v-for="(count, code) in row.failures"
                                :key="code"
                                v-bind="s.caption"
                                >{{ formatTokens(count) }} Cases would fail:
                                <code>{{ code }}</code></span
                              >
                            </Stack>
                          </td>
                          <td v-bind="s.number">{{ formatTokens(row.cases) }}</td>
                          <td v-bind="s.number">
                            <Money :amount="row.worstCase" estimated :badge="false" />
                          </td>
                        </tr>
                      </tbody>
                    </table>
                  </TableScroll>
                  <Alert v-if="anyExceeds" tone="warning"
                    >A run's worst case is above its cost cap. It may stop early with
                    <code>run_cost_cap_reached</code>, skipping the remaining Cases.</Alert
                  >
                </template>
              </Stack>
            </Section>
            <ErrorState v-if="createFailure" :error="createFailure" />
            <div>
              <Button
                type="submit"
                variant="primary"
                size="lg"
                :disabled="!taskKind"
                :loading="create.isPending.value"
                >Start
                {{
                  form.configurations.length > 1 ? `${form.configurations.length} runs` : 'run'
                }}</Button
              >
            </div>
          </Stack>
        </template>
      </Split>
    </form>
  </Container>
</template>
