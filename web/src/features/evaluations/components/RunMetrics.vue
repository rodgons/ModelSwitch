<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { table, typo } from '@/shared/components/components.styles'
import { formatPercent, formatScore, formatTokens } from '@/shared/format'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import type { RunAggregates } from '../aggregates'

/**
 * The kind-specific metrics of the runs side by side (evaluation.md section 3): per-class precision, recall and F1 with macro-F1
 * and the confusion matrix for classification, per-field accuracy and the parse failure rate for json_fields, and the judge score
 * distribution for llm_judge.
 */
const props = defineProps<{ runs: { label: string; aggregates: RunAggregates | null }[] }>()

const classification = computed(() =>
  props.runs.flatMap((r) =>
    r.aggregates?.classification ? [{ label: r.label, metrics: r.aggregates.classification }] : [],
  ),
)
const fieldRuns = computed(() =>
  props.runs.flatMap((r) =>
    r.aggregates?.jsonFields ? [{ label: r.label, metrics: r.aggregates.jsonFields }] : [],
  ),
)
const fieldNames = computed(() =>
  [...new Set(fieldRuns.value.flatMap((r) => Object.keys(r.metrics.perFieldAccuracy)))].sort(),
)
const judgeRuns = computed(() =>
  props.runs.flatMap((r) =>
    r.aggregates?.llmJudge ? [{ label: r.label, metrics: r.aggregates.llmJudge }] : [],
  ),
)
const buckets = computed(() => judgeRuns.value[0]?.metrics.distribution ?? [])
const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono, table.noWrap),
  caption: stylex.props(typo.caption),
}
</script>

<template>
  <Stack :gap="24">
    <Section
      v-for="run in classification"
      :key="run.label"
      :title="`Per-class metrics: ${run.label}`"
      surface
      flush
    >
      <Stack :gap="12">
        <TableScroll :label="`Per-class metrics of ${run.label}`">
          <table v-bind="s.table">
            <thead>
              <tr>
                <th v-bind="s.th" scope="col">Label</th>
                <th v-bind="s.thNumber" scope="col">Precision</th>
                <th v-bind="s.thNumber" scope="col">Recall</th>
                <th v-bind="s.thNumber" scope="col">F1</th>
                <th v-bind="s.thNumber" scope="col">Support</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="row in run.metrics.perClass" :key="row.label">
                <th v-bind="s.td" scope="row">
                  <span v-bind="s.mono">{{ row.label }}</span>
                </th>
                <td v-bind="s.number">{{ formatScore(row.precision) }}</td>
                <td v-bind="s.number">{{ formatScore(row.recall) }}</td>
                <td v-bind="s.number">{{ formatScore(row.f1) }}</td>
                <td v-bind="s.number">{{ formatTokens(row.support) }}</td>
              </tr>
              <tr>
                <th v-bind="s.td" scope="row">Macro-F1</th>
                <td v-bind="s.number" colspan="2" />
                <td v-bind="s.number">{{ formatScore(run.metrics.macroF1) }}</td>
                <td v-bind="s.number" />
              </tr>
            </tbody>
          </table>
        </TableScroll>
        <TableScroll :label="`Confusion matrix of ${run.label}`">
          <table v-bind="s.table">
            <caption v-bind="s.caption">
              Confusion matrix: expected label (rows) by predicted label (columns)
            </caption>
            <thead>
              <tr>
                <th v-bind="s.th" scope="col">Expected \ predicted</th>
                <th
                  v-for="column in run.metrics.confusion.columns"
                  :key="column"
                  v-bind="s.thNumber"
                  scope="col"
                >
                  {{ column }}
                </th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(row, i) in run.metrics.confusion.rows" :key="row">
                <th v-bind="s.td" scope="row">
                  <span v-bind="s.mono">{{ row }}</span>
                </th>
                <td
                  v-for="(count, j) in run.metrics.confusion.counts[i]"
                  :key="j"
                  v-bind="s.number"
                >
                  {{ formatTokens(count) }}
                </td>
              </tr>
            </tbody>
          </table>
        </TableScroll>
      </Stack>
    </Section>
    <Section v-if="fieldRuns.length" title="Per-field accuracy" surface flush>
      <TableScroll label="Per-field accuracy">
        <table v-bind="s.table">
          <thead>
            <tr>
              <th v-bind="s.th" scope="col">Field</th>
              <th v-for="run in fieldRuns" :key="run.label" v-bind="s.thNumber" scope="col">
                {{ run.label }}
              </th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="name in fieldNames" :key="name">
              <th v-bind="s.td" scope="row">
                <span v-bind="s.mono">{{ name }}</span>
              </th>
              <td v-for="run in fieldRuns" :key="run.label" v-bind="s.number">
                {{ formatPercent(run.metrics.perFieldAccuracy[name]) }}
              </td>
            </tr>
            <tr>
              <th v-bind="s.td" scope="row">Parse failure rate</th>
              <td v-for="run in fieldRuns" :key="run.label" v-bind="s.number">
                {{ formatPercent(run.metrics.parseFailureRate) }}
              </td>
            </tr>
          </tbody>
        </table>
      </TableScroll>
    </Section>
    <Section v-if="judgeRuns.length" title="Judge score distribution (model-judged)" surface flush>
      <TableScroll label="Judge score distribution">
        <table v-bind="s.table">
          <thead>
            <tr>
              <th v-bind="s.th" scope="col">Score</th>
              <th v-for="run in judgeRuns" :key="run.label" v-bind="s.thNumber" scope="col">
                {{ run.label }}
              </th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(bucket, i) in buckets" :key="i">
              <th v-bind="s.td" scope="row">
                {{ formatScore(bucket.from) }}–{{ formatScore(bucket.to) }}
              </th>
              <td v-for="run in judgeRuns" :key="run.label" v-bind="s.number">
                {{ formatTokens(run.metrics.distribution[i]?.count) }}
              </td>
            </tr>
          </tbody>
        </table>
      </TableScroll>
    </Section>
  </Stack>
</template>
