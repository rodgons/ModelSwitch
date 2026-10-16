<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, reactive, useId, watch } from 'vue'
import { api, data, type Schemas } from '@/api/client'
import Alert from '@/shared/components/Alert.vue'
import Badge from '@/shared/components/Badge.vue'
import Button from '@/shared/components/Button.vue'
import Checkbox from '@/shared/components/Checkbox.vue'
import { table, typo } from '@/shared/components/components.styles'
import EstimatedBadge from '@/shared/components/EstimatedBadge.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import Money from '@/shared/components/Money.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { formatTokens } from '@/shared/format'
import { useFormMutation } from '@/shared/forms'
import Facts from '@/shared/layout/Facts.vue'
import Grid from '@/shared/layout/Grid.vue'
import Inline from '@/shared/layout/Inline.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { useTenants } from '@/features/tenants/queries'
import { useVersions } from '../queries'
import {
  blankSimulation,
  candidateName,
  circuitLabel,
  circuitTone,
  outcomeSummary,
  simulationRequest,
  statusLabel,
  statusTone,
} from '../simulation'

/**
 * Shows the Routing Decision a hypothetical request would get from a Policy Version: the router runs on this instance's catalog,
 * Prices and circuits, and no Provider is called. Every Administrator may run it.
 */
const props = defineProps<{ policy: Schemas['PolicyView'] }>()
const form = reactive(blankSimulation())
const capabilitiesId = useId()
const versions = useVersions(() => props.policy.id)
const tenants = useTenants()

const run = useFormMutation(
  async () =>
    data(
      await api.POST('/admin/routing-policies/{policyId}/simulate', {
        params: { path: { policyId: props.policy.id } },
        body: simulationRequest(form),
      }),
    ),
  [],
)
watch(
  () => props.policy.id,
  () => {
    Object.assign(form, blankSimulation())
    run.mutation.reset()
    run.reset()
  },
)

const versionOptions = computed(() => [
  { value: '', label: `Latest (v${props.policy.version})` },
  ...(versions.data.value?.items ?? []).map((v) => ({
    value: String(v.version),
    label: `v${v.version}`,
  })),
])
const tenantOptions = computed(() => [
  { value: '', label: 'No Tenant' },
  ...(tenants.data.value?.items ?? []).map((t) => ({ value: t.id, label: t.name })),
])

const result = computed(() => run.mutation.data.value ?? null)
const summary = computed(() => (result.value ? outcomeSummary(result.value) : null))
const facts = computed(() =>
  result.value
    ? [
        { label: 'Policy Version', value: `v${result.value.version}` },
        { label: 'Strategy', value: result.value.strategy },
        {
          label: 'Rule matched',
          value: result.value.ruleMatched === null ? 'None' : `#${result.value.ruleMatched + 1}`,
        },
        {
          label: 'Output ceiling',
          value: `${formatTokens(result.value.effectiveMaxOutputTokens)} tokens`,
        },
      ]
    : [],
)
const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono),
  caption: stylex.props(typo.caption),
}
</script>

<template>
  <Stack :gap="16">
    <form novalidate @submit.prevent="run.mutation.mutate(undefined)">
      <Stack :gap="16">
        <Grid min="field" :gap="12">
          <Field v-slot="{ id }" label="Policy Version" :errors="run.errorsFor('version')">
            <SelectInput :id="id" v-model="form.version" :options="versionOptions" />
          </Field>
          <Field
            v-slot="{ id }"
            label="Tenant"
            help="Applies its per-request cost limit."
            :errors="run.errorsFor('tenantId')"
          >
            <SelectInput :id="id" v-model="form.tenantId" :options="tenantOptions" />
          </Field>
          <Field
            v-slot="{ id, invalid }"
            label="Estimated input tokens"
            :errors="run.errorsFor('estimatedInputTokens')"
          >
            <TextInput
              :id="id"
              v-model="form.estimatedInputTokens"
              type="number"
              :invalid="invalid"
            />
          </Field>
          <Field
            v-slot="{ id, invalid }"
            label="Max output tokens"
            help="Empty: the policy default."
            :errors="run.errorsFor('maxOutputTokens')"
          >
            <TextInput :id="id" v-model="form.maxOutputTokens" type="number" :invalid="invalid" />
          </Field>
          <Field
            v-slot="{ id, invalid }"
            label="Task Type"
            help="As X-ModelSwitch-Task would send it."
            :errors="run.errorsFor('taskType')"
          >
            <TextInput :id="id" v-model="form.taskType" mono :invalid="invalid" />
          </Field>
          <Field
            v-slot="{ id, invalid }"
            label="Request parameters"
            help="Comma-separated, such as temperature, seed."
            :errors="run.errorsFor('requires')"
          >
            <TextInput :id="id" v-model="form.parameters" mono :invalid="invalid" />
          </Field>
        </Grid>
        <Stack :gap="6">
          <span :id="capabilitiesId" v-bind="s.caption">Required Capabilities</span>
          <div role="group" :aria-labelledby="capabilitiesId">
            <Inline :gap="16">
              <Checkbox v-model="form.streaming" label="Streaming" />
              <Checkbox v-model="form.jsonObject" label="JSON object output" />
              <Checkbox v-model="form.jsonSchema" label="JSON-schema output" />
            </Inline>
          </div>
        </Stack>
        <div>
          <Button type="submit" :loading="run.mutation.isPending.value">Run simulation</Button>
        </div>
      </Stack>
    </form>
    <ErrorState v-if="run.generalError.value" :error="run.generalError.value" />
    <template v-if="result && summary">
      <Alert :tone="summary.tone">{{ summary.text }}</Alert>
      <Facts :items="facts" />
      <TableScroll label="Simulated Candidates">
        <table v-bind="s.table">
          <thead>
            <tr>
              <th v-bind="s.thNumber" scope="col">Rank</th>
              <th v-bind="s.th" scope="col">Provider Model</th>
              <th v-bind="s.th" scope="col">Eligibility</th>
              <th v-bind="s.th" scope="col">Circuit</th>
              <th v-bind="s.thNumber" scope="col">
                <Inline :gap="6" justify="end" :wrap="false" as="span"
                  >Worst-case cost <EstimatedBadge
                /></Inline>
              </th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="candidate in result.candidates" :key="candidate.providerModelId">
              <td v-bind="s.number">{{ candidate.rank ?? '—' }}</td>
              <td v-bind="s.td">
                <span v-bind="s.mono">{{ candidateName(candidate) }}</span>
              </td>
              <td v-bind="s.td">
                <Inline :gap="6">
                  <Badge :tone="statusTone(candidate.status)">{{
                    statusLabel[candidate.status] ?? candidate.status
                  }}</Badge>
                  <span v-if="candidate.exclusionReason" v-bind="s.mono">{{
                    candidate.exclusionReason
                  }}</span>
                </Inline>
              </td>
              <td v-bind="s.td">
                <Badge :tone="circuitTone(candidate.circuitState)">{{
                  circuitLabel[candidate.circuitState] ?? candidate.circuitState
                }}</Badge>
              </td>
              <td v-bind="s.number">
                <Money :amount="candidate.worstCaseEstimatedCost" estimated :badge="false" />
              </td>
            </tr>
          </tbody>
        </table>
      </TableScroll>
      <span v-if="!result.candidates.length" v-bind="s.caption"
        >The policy lists no Candidate for this request.</span
      >
    </template>
  </Stack>
</template>
