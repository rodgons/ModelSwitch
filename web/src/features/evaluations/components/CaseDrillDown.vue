<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import Button from '@/shared/components/Button.vue'
import { link, typo } from '@/shared/components/components.styles'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import LoadingState from '@/shared/components/LoadingState.vue'
import Money from '@/shared/components/Money.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import StatusBadge from '@/shared/components/StatusBadge.vue'
import { formatDuration, formatScore, formatTokens } from '@/shared/format'
import Facts from '@/shared/layout/Facts.vue'
import Grid from '@/shared/layout/Grid.vue'
import Inline from '@/shared/layout/Inline.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import Toolbar from '@/shared/layout/Toolbar.vue'
import { caseChoices, diffOutputs, pivotCase } from '../comparison'
import { code } from '../evaluations.styles'
import { compactJson } from '../json'
import { useAllResults } from '../runQueries'

/**
 * One Case across every run of the Comparison: each run's status, score, prediction, output and cost side by side, the request
 * that produced it, and a diff between two runs' outputs.
 */
const props = defineProps<{ runs: { runId: string; label: string }[]; initialCase?: string }>()
const loaded = ref(!!props.initialCase)
const ids = computed(() => props.runs.map((r) => r.runId))
const results = useAllResults(ids, loaded)
const pending = computed(() => results.value.some((q) => q.isPending))
const failure = computed(() => results.value.find((q) => q.error)?.error ?? null)
const perRun = computed(() =>
  props.runs.map((r, i) => ({ runId: r.runId, results: results.value[i]?.data ?? [] })),
)
const choices = computed(() => caseChoices(perRun.value))
const selected = ref(props.initialCase ?? '')
watch(choices, (list) => {
  if (!selected.value || !list.includes(selected.value)) selected.value = list[0] ?? ''
})
const pivot = computed(() => (selected.value ? pivotCase(perRun.value, selected.value) : []))
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
      <template v-if="!loaded">
        <span v-bind="s.caption"
          >Load every run's Case Results to compare one Case across runs. Large runs take a few
          seconds.</span
        >
        <div><Button @click="loaded = true">Load Case Results</Button></div>
      </template>
      <LoadingState v-else-if="pending" />
      <ErrorState v-else-if="failure" :error="failure" />
      <span v-else-if="!choices.length" v-bind="s.caption">No Case has a result yet.</span>
      <template v-else>
        <Toolbar>
          <Field v-slot="{ id }" label="Case id"
            ><SelectInput
              :id="id"
              v-model="selected"
              :options="choices.map((c) => ({ value: c, label: c }))"
          /></Field>
        </Toolbar>
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
              <pre v-bind="s.block" :aria-label="`Output of ${labelOf(entry.runId)}`">{{
                entry.result.outputText ?? '—'
              }}</pre>
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
            aria-label="Output diff"
          ><span v-for="(part, index) in diff.parts" :key="index" v-bind="s.line(part.kind)">{{ diff.mode === 'lines' ? `${part.kind === 'added' ? '+' : part.kind === 'removed' ? '-' : ' '} ${part.text}` : part.text }}</span></pre>
          <span v-bind="s.caption"
            >Removed text is struck through, added text is highlighted; JSON outputs are compared
            field by field.</span
          >
        </Stack>
      </template>
    </Stack>
  </Section>
</template>
