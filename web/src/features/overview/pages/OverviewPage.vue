<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import Button from '@/shared/components/Button.vue'
import Field from '@/shared/components/Field.vue'
import QueryView from '@/shared/components/QueryView.vue'
import SegmentedControl from '@/shared/components/SegmentedControl.vue'
import TextInput from '@/shared/components/TextInput.vue'
import TimeSeriesChart from '@/shared/components/TimeSeriesChart.vue'
import { formatDuration, formatMoney, formatPercent, formatTokens } from '@/shared/format'
import Container from '@/shared/layout/Container.vue'
import Grid from '@/shared/layout/Grid.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Readout, { type ReadoutItem } from '@/shared/layout/Readout.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import Toolbar from '@/shared/layout/Toolbar.vue'
import RequestTable from '@/features/requests/components/RequestTable.vue'
import { useOverview } from '../queries'
import { delta, fillBuckets, previousRange, type RangeKey, rangeFor } from '../range'

const key = ref<RangeKey>('24h')
const custom = reactive({ from: '', to: '' })
const applied = ref({ from: '', to: '' })
const anchor = ref(new Date())
const range = computed(() => rangeFor(key.value, anchor.value, applied.value))
const overview = useOverview(range)
const previous = useOverview(() => previousRange(range.value))
const ranges: { value: RangeKey; label: string }[] = [
  { value: '1h', label: '1 h' },
  { value: '24h', label: '24 h' },
  { value: '7d', label: '7 d' },
  { value: '30d', label: '30 d' },
  { value: 'custom', label: 'Custom' },
]

function refresh() {
  anchor.value = new Date()
}

// A quiet bucket has no requests: zero counts and no latency or rates (they are undefined, not zero).
const quiet = {
  requests: 0,
  failedRequests: 0,
  fallbackRequests: 0,
  successRate: null,
  fallbackRate: null,
  inputTokens: 0,
  outputTokens: 0,
  estimatedCost: 0,
  latencyP50Ms: null,
  latencyP95Ms: null,
  ttftP50Ms: null,
  ttftP95Ms: null,
  exact: true,
}
const points = computed(() => {
  const report = overview.data.value
  return report ? fillBuckets(report.from, report.to, report.stepSeconds, report.series, quiet) : []
})

const readout = computed<ReadoutItem[]>(() => {
  const report = overview.data.value
  if (!report) return []
  const t = report.totals
  const p = previous.data.value?.totals
  const series = points.value.map((point) => point.figures)
  return [
    {
      label: 'Requests',
      value: formatTokens(t.requests),
      delta: delta(t.requests, p?.requests) ?? undefined,
      trend: series.map((f) => f.requests),
    },
    {
      label: 'Success',
      value: formatPercent(t.successRate),
      trend: series.map((f) => f.successRate),
      tone: 'success',
    },
    {
      label: 'Cost',
      value: formatMoney(t.estimatedCost),
      estimated: true,
      delta: delta(t.estimatedCost, p?.estimatedCost) ?? undefined,
      trend: series.map((f) => f.estimatedCost),
    },
    {
      label: 'Tokens',
      value: formatTokens(t.inputTokens + t.outputTokens),
      trend: series.map((f) => f.inputTokens + f.outputTokens),
    },
    {
      label: `Latency p50 / p95${t.exact ? '' : ' (approx.)'}`,
      value: `${formatDuration(t.latencyP50Ms)} / ${formatDuration(t.latencyP95Ms)}`,
      trend: series.map((f) => f.latencyP95Ms),
    },
    {
      label: 'First Token p50 / p95',
      value: `${formatDuration(t.ttftP50Ms)} / ${formatDuration(t.ttftP95Ms)}`,
    },
    {
      label: 'Fallback',
      value: formatPercent(t.fallbackRate),
      trend: series.map((f) => f.fallbackRate),
      tone: 'warning',
    },
  ]
})

const volume = computed(() => {
  const series = points.value
  return [
    {
      name: 'Succeeded or cancelled',
      points: series.map(
        (p) => [p.start, p.figures.requests - p.figures.failedRequests] as [string, number],
      ),
    },
    {
      name: 'Failed',
      points: series.map((p) => [p.start, p.figures.failedRequests] as [string, number]),
    },
  ]
})
const latency = computed(() => {
  const series = points.value
  return [
    {
      name: 'p50',
      points: series.map((p) => [p.start, p.figures.latencyP50Ms] as [string, number | null]),
    },
    {
      name: 'p95',
      points: series.map((p) => [p.start, p.figures.latencyP95Ms] as [string, number | null]),
    },
  ]
})
const fallback = computed(() => [
  {
    name: 'Fallback rate',
    points: points.value.map((p) => [p.start, p.figures.fallbackRate] as [string, number | null]),
  },
])
const cost = computed(() => [
  {
    name: 'Estimated Cost',
    points: points.value.map((p) => [p.start, p.figures.estimatedCost] as [string, number]),
  },
])
const count = (value: number | null) => formatTokens(value)
</script>

<template>
  <Container>
    <PageHeader
      title="Overview"
      lede="Traffic, outcomes and Estimated Cost across the Tenants you can see."
    >
      <template #actions>
        <SegmentedControl
          v-model="key"
          label="Time range"
          :options="ranges"
          @update:model-value="refresh"
        />
      </template>
    </PageHeader>
    <Stack :gap="48">
      <form v-if="key === 'custom'" @submit.prevent="applied = { ...custom }">
        <Toolbar>
          <Field v-slot="{ id }" label="From (UTC)"
            ><TextInput :id="id" v-model="custom.from" type="datetime-local"
          /></Field>
          <Field v-slot="{ id }" label="To (UTC)"
            ><TextInput :id="id" v-model="custom.to" type="datetime-local"
          /></Field>
          <Button type="submit" variant="primary">Show range</Button>
        </Toolbar>
      </form>
      <QueryView
        :data="overview.data.value"
        :is-pending="overview.isPending.value"
        :error="overview.error.value"
        :refetch="overview.refetch"
        :is-empty="(report) => report.totals.requests === 0"
        empty-text="No Inference Request in this range yet. Send one with an API Key, or widen the range."
      >
        <template #default="{ data: report }">
          <Stack :gap="48">
            <Readout :items="readout" />
            <Grid min="chart" :gap="24">
              <Section title="Requests over time" surface>
                <TimeSeriesChart
                  label="Requests over time by outcome"
                  :series="volume"
                  :format="count"
                />
              </Section>
              <Section title="Latency" surface>
                <TimeSeriesChart
                  label="Latency p50 and p95"
                  :series="latency"
                  :format="formatDuration"
                />
              </Section>
              <Section title="Fallback rate" surface>
                <TimeSeriesChart label="Fallback rate" :series="fallback" :format="formatPercent" />
              </Section>
              <Section title="Estimated Cost" surface>
                <TimeSeriesChart
                  label="Estimated Cost over time"
                  :series="cost"
                  :format="formatMoney"
                />
              </Section>
            </Grid>
            <Section title="Recent failed requests" surface flush>
              <RequestTable
                v-if="report.recentFailures.length"
                :items="report.recentFailures"
                label="Recent failed requests"
                compact
              />
              <p v-else>No failed request in this range.</p>
            </Section>
          </Stack>
        </template>
      </QueryView>
    </Stack>
  </Container>
</template>
