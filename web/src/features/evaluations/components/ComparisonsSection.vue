<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, reactive, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import Button from '@/shared/components/Button.vue'
import { link, table, typo } from '@/shared/components/components.styles'
import Field from '@/shared/components/Field.vue'
import QueryView from '@/shared/components/QueryView.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import StatusBadge from '@/shared/components/StatusBadge.vue'
import { formatScore, formatTimestamp, formatTokens } from '@/shared/format'
import Inline from '@/shared/layout/Inline.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import Toolbar from '@/shared/layout/Toolbar.vue'
import { firstPage, nextPage, previousPage } from '../pager'
import { useDatasets } from '../queries'
import { useComparisons } from '../runQueries'

/** Comparisons newest first, each run on its own row with status and live progress (polled while any run is unfinished). */
const props = defineProps<{ tenantId: string; tenantName: (id: string) => string }>()
const filters = reactive({ datasetId: '', status: '' })
const page = ref(firstPage())
watch(
  () => [props.tenantId, filters.datasetId, filters.status],
  () => (page.value = firstPage()),
)
watch(
  () => props.tenantId,
  () => (filters.datasetId = ''),
)
const comparisons = useComparisons(
  () => ({ tenantId: props.tenantId, datasetId: filters.datasetId, status: filters.status }),
  () => page.value.cursor,
)
const datasets = useDatasets(
  () => props.tenantId,
  () => null,
)
const datasetNames = computed(
  () => new Map((datasets.data.value?.items ?? []).map((d) => [d.id, d.name] as const)),
)
const datasetOptions = computed(() => [
  { value: '', label: 'Any Dataset' },
  ...(datasets.data.value?.items ?? []).map((d) => ({ value: d.id, label: d.name })),
])
const statusOptions = [
  { value: '', label: 'Any status' },
  { value: 'queued', label: 'Queued' },
  { value: 'running', label: 'Running' },
  { value: 'succeeded', label: 'Succeeded' },
  { value: 'failed', label: 'Failed' },
  { value: 'cancelled', label: 'Cancelled' },
]
const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  name: stylex.props(link.quiet, link.focus),
  mono: stylex.props(typo.mono, table.noWrap),
  caption: stylex.props(typo.caption),
}
</script>

<template>
  <Stack :gap="12">
    <Toolbar>
      <Field v-slot="{ id }" label="Dataset"
        ><SelectInput :id="id" v-model="filters.datasetId" :options="datasetOptions"
      /></Field>
      <Field v-slot="{ id }" label="Run status"
        ><SelectInput :id="id" v-model="filters.status" :options="statusOptions"
      /></Field>
    </Toolbar>
    <Section title="Comparisons" surface flush>
      <QueryView
        :data="comparisons.data.value"
        :is-pending="comparisons.isPending.value"
        :error="comparisons.error.value"
        :refetch="comparisons.refetch"
        :is-empty="(result) => result.items.length === 0"
        empty-text="No Comparison matches. Start a run to compare models, Prompts or parameters on a Dataset Version."
      >
        <template #default="{ data: result }">
          <TableScroll label="Comparisons">
            <table v-bind="s.table">
              <thead>
                <tr>
                  <th v-bind="s.th" scope="col">Comparison</th>
                  <th v-bind="s.th" scope="col">Target</th>
                  <th v-bind="s.th" scope="col">Evaluator</th>
                  <th v-bind="s.th" scope="col">Status</th>
                  <th v-bind="s.thNumber" scope="col">Progress</th>
                  <th v-bind="s.thNumber" scope="col">Quality</th>
                </tr>
              </thead>
              <tbody>
                <template v-for="comparison in result.items" :key="comparison.id">
                  <tr v-for="(run, index) in comparison.runs" :key="run.id">
                    <td
                      v-if="index === 0"
                      v-bind="s.td"
                      :rowspan="Math.max(1, comparison.runs.length)"
                    >
                      <Stack :gap="2">
                        <RouterLink :to="`/comparisons/${comparison.id}`" v-bind="s.name">{{
                          comparison.name
                        }}</RouterLink>
                        <span v-bind="s.caption"
                          >{{ datasetNames.get(comparison.datasetId) ?? 'Dataset' }} v{{
                            comparison.datasetVersion
                          }}, {{ tenantName(comparison.tenantId) }}</span
                        >
                        <span v-bind="s.caption">{{ formatTimestamp(comparison.createdAt) }}</span>
                      </Stack>
                    </td>
                    <td v-bind="s.td">
                      <span v-bind="s.mono">{{ run.target.label }}</span>
                    </td>
                    <td v-bind="s.td">{{ run.evaluator.kind }} v{{ run.evaluator.version }}</td>
                    <td v-bind="s.td"><StatusBadge :status="run.status" /></td>
                    <td v-bind="s.number">
                      {{ formatTokens(run.casesDone) }} / {{ formatTokens(run.casesTotal) }}
                    </td>
                    <td v-bind="s.number">{{ formatScore(run.quality?.value) }}</td>
                  </tr>
                </template>
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
        :disabled="!comparisons.data.value?.nextCursor"
        @click="page = nextPage(page, comparisons.data.value?.nextCursor ?? '')"
        >Next page</Button
      >
    </Inline>
  </Stack>
</template>
