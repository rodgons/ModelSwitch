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
import { firstPage, nextPage, previousPage } from '../pager'
import { usePrompts } from '../queries'

/** Prompts in the Tenant filter (or every Tenant in the grant), newest first. */
const props = defineProps<{ tenantId: string; tenantName: (id: string) => string }>()
const page = ref(firstPage())
watch(
  () => props.tenantId,
  () => (page.value = firstPage()),
)
const prompts = usePrompts(
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
    <Section title="Prompts" surface flush>
      <QueryView
        :data="prompts.data.value"
        :is-pending="prompts.isPending.value"
        :error="prompts.error.value"
        :refetch="prompts.refetch"
        :is-empty="(result) => result.items.length === 0"
        empty-text="No Prompt yet. Create one, then write its first Prompt Version."
      >
        <template #default="{ data: result }">
          <TableScroll label="Prompts">
            <table v-bind="s.table">
              <thead>
                <tr>
                  <th v-bind="s.th" scope="col">Name</th>
                  <th v-bind="s.th" scope="col">Tenant</th>
                  <th v-bind="s.thNumber" scope="col">Latest version</th>
                  <th v-bind="s.th" scope="col">Created</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="prompt in result.items" :key="prompt.id">
                  <td v-bind="s.td">
                    <RouterLink :to="`/evaluations/prompts/${prompt.id}`" v-bind="s.name">{{
                      prompt.name
                    }}</RouterLink>
                  </td>
                  <td v-bind="s.td">{{ tenantName(prompt.tenantId) }}</td>
                  <td v-bind="s.number">
                    {{ prompt.latestVersion ? `v${formatTokens(prompt.latestVersion)}` : 'None' }}
                  </td>
                  <td v-bind="s.td">{{ formatTimestamp(prompt.createdAt) }}</td>
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
        :disabled="!prompts.data.value?.nextCursor"
        @click="page = nextPage(page, prompts.data.value?.nextCursor ?? '')"
        >Next page</Button
      >
    </Inline>
  </Stack>
</template>
