<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import Button from '@/shared/components/Button.vue'
import { link, table } from '@/shared/components/components.styles'
import QueryView from '@/shared/components/QueryView.vue'
import { formatTimestamp, formatTokens } from '@/shared/format'
import Inline from '@/shared/layout/Inline.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { taskKindLabel } from '../labels'
import { firstPage, nextPage, previousPage } from '../pager'
import { useDatasets } from '../queries'

/** Datasets in the Tenant filter (or every Tenant in the grant), newest first. */
const props = defineProps<{ tenantId: string; tenantName: (id: string) => string }>()
const page = ref(firstPage())
watch(
  () => props.tenantId,
  () => (page.value = firstPage()),
)
const datasets = useDatasets(
  () => props.tenantId,
  () => page.value.cursor,
)
const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  name: stylex.props(link.quiet, link.focus),
}
</script>

<template>
  <Stack :gap="12">
    <Section title="Datasets" surface flush>
      <QueryView
        :data="datasets.data.value"
        :is-pending="datasets.isPending.value"
        :error="datasets.error.value"
        :refetch="datasets.refetch"
        :is-empty="(result) => result.items.length === 0"
        empty-text="No Dataset yet. Create one, then upload its Cases as a JSONL file."
      >
        <template #default="{ data: result }">
          <TableScroll label="Datasets">
            <table v-bind="s.table">
              <thead>
                <tr>
                  <th v-bind="s.th" scope="col">Name</th>
                  <th v-bind="s.th" scope="col">Tenant</th>
                  <th v-bind="s.th" scope="col">Task kind</th>
                  <th v-bind="s.thNumber" scope="col">Latest version</th>
                  <th v-bind="s.th" scope="col">Created</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="dataset in result.items" :key="dataset.id">
                  <td v-bind="s.td">
                    <RouterLink :to="`/evaluations/datasets/${dataset.id}`" v-bind="s.name">{{
                      dataset.name
                    }}</RouterLink>
                  </td>
                  <td v-bind="s.td">{{ tenantName(dataset.tenantId) }}</td>
                  <td v-bind="s.td">{{ taskKindLabel(dataset.taskKind) }}</td>
                  <td v-bind="s.number">
                    {{ dataset.latestVersion ? `v${formatTokens(dataset.latestVersion)}` : 'None' }}
                  </td>
                  <td v-bind="s.td">{{ formatTimestamp(dataset.createdAt) }}</td>
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
        :disabled="!datasets.data.value?.nextCursor"
        @click="page = nextPage(page, datasets.data.value?.nextCursor ?? '')"
        >Next page</Button
      >
    </Inline>
  </Stack>
</template>
