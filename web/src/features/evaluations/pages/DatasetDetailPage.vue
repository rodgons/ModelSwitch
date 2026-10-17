<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import { useSession } from '@/app/session'
import { useTenants } from '@/features/tenants/queries'
import { link, table } from '@/shared/components/components.styles'
import Identifier from '@/shared/components/Identifier.vue'
import QueryView from '@/shared/components/QueryView.vue'
import { formatTimestamp, formatTokens } from '@/shared/format'
import Container from '@/shared/layout/Container.vue'
import Facts from '@/shared/layout/Facts.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Split from '@/shared/layout/Split.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import DeleteAction from '../components/DeleteAction.vue'
import UploadVersionForm from '../components/UploadVersionForm.vue'
import { taskKindLabel } from '../labels'
import { deleteDataset, evaluationKeys, useDataset, useDatasetVersions } from '../queries'

const props = defineProps<{ id: string }>()
const session = useSession()
const dataset = useDataset(() => props.id)
const versions = useDatasetVersions(() => props.id)
const tenants = useTenants()
const tenantName = (id: string) => tenants.data.value?.items.find((t) => t.id === id)?.name ?? id
const canWrite = computed(() => session.can('operator'))
const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  name: stylex.props(link.quiet, link.focus),
  back: stylex.props(link.quiet, link.focus),
}
</script>

<template>
  <Container>
    <QueryView
      :data="dataset.data.value"
      :is-pending="dataset.isPending.value"
      :error="dataset.error.value"
      :refetch="dataset.refetch"
    >
      <template #default="{ data: detail }">
        <PageHeader :title="detail.name" lede="Dataset">
          <template #actions>
            <Inline :gap="12">
              <RouterLink to="/evaluations" v-bind="s.back">All Datasets</RouterLink>
              <DeleteAction
                v-if="canWrite"
                kind="Dataset"
                :name="detail.name"
                :remove="() => deleteDataset(detail.id)"
                :invalidate="evaluationKeys.datasets"
                to="/evaluations?tab=datasets"
              >
                <p>
                  Every Dataset Version and its Cases are deleted. A Dataset that a Comparison still
                  uses cannot be deleted: delete those Comparisons first.
                </p>
              </DeleteAction>
            </Inline>
          </template>
        </PageHeader>
        <Stack :gap="48">
          <Facts
            :items="[
              { label: 'Tenant', value: tenantName(detail.tenantId) },
              { label: 'Task kind', value: taskKindLabel(detail.taskKind) },
              {
                label: 'Latest version',
                value: detail.latestVersion ? `v${detail.latestVersion}` : 'None',
              },
              { label: 'Created', value: formatTimestamp(detail.createdAt) },
              { label: 'Dataset id', value: '', key: 'id' },
            ]"
          >
            <template #id><Identifier :value="detail.id" copy /></template>
          </Facts>
          <Split>
            <Section title="Dataset Versions" surface flush>
              <QueryView
                :data="versions.data.value"
                :is-pending="versions.isPending.value"
                :error="versions.error.value"
                :refetch="versions.refetch"
                :is-empty="(page) => page.items.length === 0"
                :empty-text="
                  canWrite
                    ? 'No version yet. Upload a JSONL file of Cases to create version 1.'
                    : 'No version yet. An Operator can upload a JSONL file of Cases.'
                "
              >
                <template #default="{ data: page }">
                  <TableScroll label="Dataset Versions">
                    <table v-bind="s.table">
                      <thead>
                        <tr>
                          <th v-bind="s.th" scope="col">Version</th>
                          <th v-bind="s.thNumber" scope="col">Cases</th>
                          <th v-bind="s.th" scope="col">Content SHA-256</th>
                          <th v-bind="s.th" scope="col">Created</th>
                        </tr>
                      </thead>
                      <tbody>
                        <tr v-for="version in page.items" :key="version.version">
                          <td v-bind="s.td">
                            <RouterLink
                              :to="`/evaluations/datasets/${detail.id}/versions/${version.version}`"
                              v-bind="s.name"
                              >v{{ version.version }}</RouterLink
                            >
                          </td>
                          <td v-bind="s.number">{{ formatTokens(version.caseCount) }}</td>
                          <td v-bind="s.td"><Identifier :value="version.contentSha256" /></td>
                          <td v-bind="s.td">{{ formatTimestamp(version.createdAt) }}</td>
                        </tr>
                      </tbody>
                    </table>
                  </TableScroll>
                </template>
              </QueryView>
            </Section>
            <template #aside>
              <Section v-if="canWrite" title="Upload a new version" surface>
                <UploadVersionForm :dataset-id="detail.id" :task-kind="detail.taskKind" />
              </Section>
            </template>
          </Split>
        </Stack>
      </template>
    </QueryView>
  </Container>
</template>
