<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import Button from '@/shared/components/Button.vue'
import { link, typo } from '@/shared/components/components.styles'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import LoadingState from '@/shared/components/LoadingState.vue'
import Money from '@/shared/components/Money.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import StatusBadge from '@/shared/components/StatusBadge.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { formatDuration, formatScore, formatTokens } from '@/shared/format'
import Facts from '@/shared/layout/Facts.vue'
import Grid from '@/shared/layout/Grid.vue'
import Inline from '@/shared/layout/Inline.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import Toolbar from '@/shared/layout/Toolbar.vue'
import { diffLinePrefix, diffOutputs } from '../comparison'
import { code } from '../evaluations.styles'
import { compactJson } from '../json'
import { useCaseResults } from '../runQueries'

/**
 * One Case across every run of the Comparison: each run's status, score, prediction, output and cost side by side, the request
 * that produced it, and a diff between two runs' outputs. The Case is chosen by its dataset id, and each run is asked for that one
 * Case's result (one request per run).
 */
const props = defineProps<{ runs: { runId: string; label: string }[]; initialCase?: string }>()
const ids = computed(() => props.runs.map((r) => r.runId))
const caseInput = ref(props.initialCase ?? '')
const selected = ref(props.initialCase ?? '')
const show = () => {
  selected.value = caseInput.value.trim()
}
const results = useCaseResults(ids, selected)
const pending = computed(() => results.value.some((q) => q.isPending))
const failure = computed(() => results.value.find((q) => q.error)?.error ?? null)
/** Each run's result for the chosen Case, in run order; null where the run has none (not reached yet, or cancelled before it). */
const pivot = computed(() =>
  props.runs.map((r, i) => ({ runId: r.runId, result: results.value[i]?.data ?? null })),
)
const found = computed(() => pivot.value.some((p) => p.result))
const labelOf = (runId: string) => props.runs.find((r) => r.runId === runId)?.label ?? runId

const left = ref(props.runs[0]?.runId ?? '')
const right = ref(props.runs[1]?.runId ?? props.runs[0]?.runId ?? '')
const runOptions = computed(() => props.runs.map((r) => ({ value: r.runId, label: r.label })))
const diff = computed(() => {
  const a = pivot.value.find((p) => p.runId === left.value)?.result
  const b = pivot.value.find((p) => p.runId === right.value)?.result
  return a || b ? diffOutputs(a?.outputText ?? null, b?.outputText ?? null) : null
})

const s = {
  block: stylex.props(code.block, code.wrap),
  line: (kind: 'same' | 'added' | 'removed') =>
    stylex.props(
      diff.value?.mode === 'lines' && code.line,
      kind === 'added' && code.added,
      kind === 'removed' && code.removed,
    ),
  caption: stylex.props(typo.caption),
  mono: stylex.props(typo.mono),
  link: stylex.props(link.inline, link.focus),
}
</script>

<template>
  <Section title="Case drill-down" surface>
    <Stack :gap="16">
      <form novalidate @submit.prevent="show">
        <Toolbar>
          <Field v-slot="{ id }" label="Case id" help="The Case's id in the dataset, exactly."
            ><TextInput :id="id" v-model="caseInput" mono
          /></Field>
          <Button type="submit">Show</Button>
        </Toolbar>
      </form>
      <span v-if="!selected" v-bind="s.caption"
        >Enter a Case id to compare that Case across runs.</span
      >
      <LoadingState v-else-if="pending" />
      <ErrorState v-else-if="failure" :error="failure" />
      <span v-else-if="!found" v-bind="s.caption"
        >No run has a result for Case <code>{{ selected }}</code> yet.</span
      >
      <template v-else>
        <Grid min="card" :gap="16">
          <Stack v-for="entry in pivot" :key="entry.runId" :gap="8">
            <span v-bind="s.mono">{{ labelOf(entry.runId) }}</span>
            <span v-if="!entry.result" v-bind="s.caption">No result for this Case yet.</span>
            <template v-else>
              <Inline :gap="8">
                <StatusBadge :status="entry.result.status" />
                <code v-if="entry.result.errorCode">{{ entry.result.errorCode }}</code>
              </Inline>
              <Facts
                :items="[
                  { label: 'Score', value: formatScore(entry.result.score) },
                  { label: 'Predicted', value: compactJson(entry.result.predicted, 40).text },
                  { label: 'Latency', value: formatDuration(entry.result.latencyMs) },
                  {
                    label: 'Tokens',
                    value: `${formatTokens(entry.result.inputTokens)} in, ${formatTokens(entry.result.outputTokens)} out`,
                  },
                  { label: 'Cost', value: '', key: 'cost' },
                  ...(entry.result.judgeInferenceRequestId
                    ? [{ label: 'Judge cost', value: '', key: 'judge' }]
                    : []),
                ]"
              >
                <template #cost><Money :amount="entry.result.estimatedCost" estimated /></template>
                <template #judge
                  ><Money :amount="entry.result.judgeEstimatedCost" estimated
                /></template>
              </Facts>
              <pre
                v-bind="s.block"
                role="region"
                tabindex="0"
                :aria-label="`Output of ${labelOf(entry.runId)}`"
                >{{ entry.result.outputText ?? '—' }}</pre>
              <Inline :gap="12">
                <RouterLink
                  v-if="entry.result.inferenceRequestId"
                  :to="`/requests/${entry.result.inferenceRequestId}`"
                  v-bind="s.link"
                  >Candidate request</RouterLink
                >
                <RouterLink
                  v-if="entry.result.judgeInferenceRequestId"
                  :to="`/requests/${entry.result.judgeInferenceRequestId}`"
                  v-bind="s.link"
                  >Judge request</RouterLink
                >
              </Inline>
            </template>
          </Stack>
        </Grid>
        <Stack v-if="runs.length > 1" :gap="8">
          <Toolbar>
            <Field v-slot="{ id }" label="Compare output of"
              ><SelectInput :id="id" v-model="left" :options="runOptions"
            /></Field>
            <Field v-slot="{ id }" label="with"
              ><SelectInput :id="id" v-model="right" :options="runOptions"
            /></Field>
          </Toolbar>
          <pre
            v-if="diff"
            v-bind="s.block"
            role="region"
            tabindex="0"
            aria-label="Output diff"
          ><span v-for="(part, index) in diff.parts" :key="index" v-bind="s.line(part.kind)">{{ diff.mode === 'lines' ? `${diffLinePrefix(part.kind)} ${part.text}` : part.text }}</span></pre>
          <span v-bind="s.caption"
            >Removed text is struck through, added text is highlighted; JSON outputs are compared
            field by field.</span
          >
        </Stack>
      </template>
    </Stack>
  </Section>
</template>
