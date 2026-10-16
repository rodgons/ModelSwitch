<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useSession } from '@/app/session'
import Button from '@/shared/components/Button.vue'
import { link, table, typo } from '@/shared/components/components.styles'
import Identifier from '@/shared/components/Identifier.vue'
import QueryView from '@/shared/components/QueryView.vue'
import { formatTimestamp, formatTokens } from '@/shared/format'
import Container from '@/shared/layout/Container.vue'
import Facts from '@/shared/layout/Facts.vue'
import Grid from '@/shared/layout/Grid.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { code } from '../evaluations.styles'
import { compactJson, prettyJson } from '../json'
import { firstPage, nextPage, previousPage } from '../pager'
import { useCases, useDataset, useDatasetVersion } from '../queries'

/** One immutable Dataset Version: its hash, scoring schema and Cases in upload order. */
const props = defineProps<{ id: string; version: string }>()
const session = useSession()
const number = computed(() => Number(props.version))
const dataset = useDataset(() => props.id)
const detail = useDatasetVersion(() => props.id, number)
const page = ref(firstPage())
const expanded = ref(new Set<string>())
watch(
  () => [props.id, props.version],
  () => {
    page.value = firstPage()
    expanded.value = new Set()
  },
)
const cases = useCases(
  () => props.id,
  number,
  () => page.value.cursor,
)

function toggle(caseId: string) {
  const next = new Set(expanded.value)
  if (!next.delete(caseId)) next.add(caseId)
  expanded.value = next
}
const columns = ['input', 'expected', 'metadata'] as const

const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td, code.cell),
  number: stylex.props(table.td, table.number, code.cell),
  mono: stylex.props(typo.mono, table.noWrap),
  inline: stylex.props(code.inline),
  block: stylex.props(code.block),
  wrapBlock: stylex.props(code.block, code.wrap),
  caption: stylex.props(typo.caption),
  back: stylex.props(link.quiet, link.focus),
}
</script>

<template>
  <Container size="wide">
    <PageHeader
      :title="`${dataset.data.value?.name ?? 'Dataset'} v${version}`"
      lede="Dataset Version. Its Cases never change; a new upload makes a new version."
    >
      <template #actions>
        <Inline :gap="12">
          <RouterLink :to="`/evaluations/datasets/${id}`" v-bind="s.back">All versions</RouterLink>
          <Button
            v-if="session.can('operator')"
            variant="primary"
            :to="{ path: '/evaluations/new', query: { datasetId: id, version } }"
            >Start a run</Button
          >
        </Inline>
      </template>
    </PageHeader>
    <Stack :gap="48">
      <QueryView
        :data="detail.data.value"
        :is-pending="detail.isPending.value"
        :error="detail.error.value"
        :refetch="detail.refetch"
      >
        <template #default="{ data: v }">
          <Stack :gap="24">
            <Facts
              :items="[
                { label: 'Cases', value: formatTokens(v.caseCount) },
                { label: 'Content SHA-256', value: '', key: 'sha' },
                { label: 'Created', value: formatTimestamp(v.createdAt) },
              ]"
            >
              <template #sha><Identifier :value="v.contentSha256" :head="12" copy /></template>
            </Facts>
            <Section title="Scoring schema">
              <Stack :gap="8">
                <span v-bind="s.caption"
                  >Derived from the Cases' expected values: the labels or fields Evaluators score
                  against.</span
                >
                <pre v-bind="s.wrapBlock" aria-label="Scoring schema">{{
                  prettyJson(v.scoringSchema)
                }}</pre>
              </Stack>
            </Section>
          </Stack>
        </template>
      </QueryView>
      <Stack :gap="12">
        <Section title="Cases" surface flush>
          <QueryView
            :data="cases.data.value"
            :is-pending="cases.isPending.value"
            :error="cases.error.value"
            :refetch="cases.refetch"
            :is-empty="(result) => result.items.length === 0"
            empty-text="This version has no Cases."
          >
            <template #default="{ data: result }">
              <TableScroll label="Cases">
                <table v-bind="s.table">
                  <thead>
                    <tr>
                      <th v-bind="s.thNumber" scope="col">#</th>
                      <th v-bind="s.th" scope="col">Case id</th>
                      <th v-bind="s.th" scope="col">Input</th>
                      <th v-bind="s.th" scope="col">Expected</th>
                      <th v-bind="s.th" scope="col">Metadata</th>
                      <th v-bind="s.th" scope="col"><span v-bind="s.caption">Details</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    <template v-for="item in result.items" :key="item.id">
                      <tr>
                        <td v-bind="s.number">{{ formatTokens(item.ordinal + 1) }}</td>
                        <td v-bind="s.td">
                          <span v-bind="s.mono" :title="item.externalId">{{
                            item.externalId
                          }}</span>
                        </td>
                        <td v-for="column in columns" :key="column" v-bind="s.td">
                          <span v-bind="s.inline">{{ compactJson(item[column], 60).text }}</span>
                        </td>
                        <td v-bind="s.td">
                          <Button
                            variant="ghost"
                            size="sm"
                            :aria-expanded="expanded.has(item.id)"
                            :aria-controls="`case-${item.id}`"
                            :aria-label="`${expanded.has(item.id) ? 'Collapse' : 'Expand'} Case ${item.externalId}`"
                            @click="toggle(item.id)"
                            >{{ expanded.has(item.id) ? 'Collapse' : 'Expand' }}</Button
                          >
                        </td>
                      </tr>
                      <tr v-if="expanded.has(item.id)" :id="`case-${item.id}`">
                        <td v-bind="s.td" colspan="6">
                          <Grid min="card" :gap="12">
                            <Stack v-for="column in columns" :key="column" :gap="6">
                              <span v-bind="s.caption">{{
                                column.charAt(0).toUpperCase() + column.slice(1)
                              }}</span>
                              <pre v-bind="s.wrapBlock">{{ prettyJson(item[column]) }}</pre>
                            </Stack>
                          </Grid>
                        </td>
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
            :disabled="!cases.data.value?.nextCursor"
            @click="page = nextPage(page, cases.data.value?.nextCursor ?? '')"
            >Next page</Button
          >
        </Inline>
      </Stack>
    </Stack>
  </Container>
</template>
