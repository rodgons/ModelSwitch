<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { useSession } from '@/app/session'
import { useTenants } from '@/features/tenants/queries'
import Badge from '@/shared/components/Badge.vue'
import Button from '@/shared/components/Button.vue'
import { link, table, typo } from '@/shared/components/components.styles'
import EmptyState from '@/shared/components/EmptyState.vue'
import Identifier from '@/shared/components/Identifier.vue'
import QueryView from '@/shared/components/QueryView.vue'
import { formatTimestamp } from '@/shared/format'
import Container from '@/shared/layout/Container.vue'
import Facts from '@/shared/layout/Facts.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Split from '@/shared/layout/Split.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import PromptVersionEditor from '../components/PromptVersionEditor.vue'
import { code } from '../evaluations.styles'
import { prettyJson } from '../json'
import { usePrompt, usePromptVersions } from '../queries'
import { detectVariables } from '../template'

/** A Prompt and its immutable Prompt Versions; the version shown is in the URL (`?version=`), the latest by default. */
const props = defineProps<{ id: string }>()
const session = useSession()
const route = useRoute()
const router = useRouter()
const prompt = usePrompt(() => props.id)
const versions = usePromptVersions(() => props.id)
const tenants = useTenants()
const tenantName = (id: string) => tenants.data.value?.items.find((t) => t.id === id)?.name ?? id
const canWrite = computed(() => session.can('operator'))

const selected = computed(() => {
  const items = versions.data.value?.items ?? []
  const wanted = Number(route.query.version)
  return items.find((v) => v.version === wanted) ?? items[0] ?? null
})
const select = (version: number) =>
  void router.replace({ query: { ...route.query, version: String(version) } })
const variables = computed(() =>
  selected.value ? detectVariables(selected.value.template.messages) : [],
)

const editing = ref(false)
watch(
  () => props.id,
  () => (editing.value = false),
)
function saved(version: number) {
  editing.value = false
  select(version)
}

const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  td: stylex.props(table.td),
  row: (active: boolean) => stylex.props(table.row, active && table.highlighted),
  versionLink: stylex.props(link.quiet, link.focus),
  back: stylex.props(link.quiet, link.focus),
  block: stylex.props(code.block, code.wrap),
  caption: stylex.props(typo.caption),
  mono: stylex.props(typo.mono),
}
</script>

<template>
  <Container>
    <QueryView
      :data="prompt.data.value"
      :is-pending="prompt.isPending.value"
      :error="prompt.error.value"
      :refetch="prompt.refetch"
    >
      <template #default="{ data: detail }">
        <PageHeader :title="detail.name" lede="Prompt">
          <template #actions>
            <Inline :gap="12">
              <RouterLink to="/evaluations?tab=prompts" v-bind="s.back">All Prompts</RouterLink>
              <Button v-if="canWrite && !editing" variant="primary" @click="editing = true"
                >Create version</Button
              >
            </Inline>
          </template>
        </PageHeader>
        <Stack :gap="48">
          <Facts
            :items="[
              { label: 'Tenant', value: tenantName(detail.tenantId) },
              {
                label: 'Latest version',
                value: detail.latestVersion ? `v${detail.latestVersion}` : 'None',
              },
              { label: 'Created', value: formatTimestamp(detail.createdAt) },
              { label: 'Prompt id', value: '', key: 'id' },
            ]"
          >
            <template #id><Identifier :value="detail.id" copy /></template>
          </Facts>
          <Split>
            <QueryView
              :data="versions.data.value"
              :is-pending="versions.isPending.value"
              :error="versions.error.value"
              :refetch="versions.refetch"
            >
              <template #default>
                <PromptVersionEditor
                  v-if="editing"
                  :prompt-id="detail.id"
                  :from="selected"
                  @saved="saved"
                  @cancel="editing = false"
                />
                <Section v-else-if="selected" :title="`Prompt Version v${selected.version}`">
                  <Stack :gap="16">
                    <Stack
                      v-for="(message, index) in selected.template.messages"
                      :key="index"
                      :gap="6"
                    >
                      <Inline :gap="8">
                        <Badge
                          :tone="message.role === 'system' ? 'neutral' : 'info'"
                          :dot="false"
                          >{{ message.role }}</Badge
                        >
                        <span v-bind="s.caption">Message {{ index + 1 }}</span>
                      </Inline>
                      <pre v-bind="s.block">{{ message.content }}</pre>
                    </Stack>
                    <Stack :gap="6">
                      <span v-bind="s.caption">Variables each Case input must have</span>
                      <Inline v-if="variables.length" :gap="6">
                        <Badge v-for="name in variables" :key="name" tone="accent" :dot="false"
                          ><span v-bind="s.mono">{{ name }}</span></Badge
                        >
                      </Inline>
                      <span v-else v-bind="s.caption">None</span>
                    </Stack>
                    <Stack :gap="6">
                      <span v-bind="s.caption">Response format</span>
                      <pre v-if="selected.template.responseFormat" v-bind="s.block">{{
                        prettyJson(selected.template.responseFormat)
                      }}</pre>
                      <span v-else v-bind="s.caption">None: the model replies in text.</span>
                    </Stack>
                  </Stack>
                </Section>
                <EmptyState
                  v-else
                  :text="
                    canWrite
                      ? 'No version yet. Write the messages of version 1.'
                      : 'No version yet. An Operator can write version 1.'
                  "
                >
                  <Button v-if="canWrite" variant="primary" @click="editing = true"
                    >Create version</Button
                  >
                </EmptyState>
              </template>
            </QueryView>
            <template #aside>
              <Section title="Prompt Versions" surface flush>
                <QueryView
                  :data="versions.data.value"
                  :is-pending="versions.isPending.value"
                  :error="versions.error.value"
                  :is-empty="(page) => page.items.length === 0"
                  empty-text="No version yet."
                >
                  <template #default="{ data: page }">
                    <TableScroll label="Prompt Versions">
                      <table v-bind="s.table">
                        <thead>
                          <tr>
                            <th v-bind="s.th" scope="col">Version</th>
                            <th v-bind="s.th" scope="col">Created</th>
                          </tr>
                        </thead>
                        <tbody>
                          <tr
                            v-for="version in page.items"
                            :key="version.version"
                            v-bind="s.row(version.version === selected?.version)"
                          >
                            <td v-bind="s.td">
                              <RouterLink
                                :to="{ query: { ...route.query, version: version.version } }"
                                v-bind="s.versionLink"
                                :aria-current="
                                  version.version === selected?.version ? 'true' : undefined
                                "
                                >v{{ version.version }}</RouterLink
                              >
                            </td>
                            <td v-bind="s.td">{{ formatTimestamp(version.createdAt) }}</td>
                          </tr>
                        </tbody>
                      </table>
                    </TableScroll>
                  </template>
                </QueryView>
              </Section>
            </template>
          </Split>
        </Stack>
      </template>
    </QueryView>
  </Container>
</template>
