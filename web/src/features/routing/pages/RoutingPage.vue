<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { useMutation, useQueryClient } from '@tanstack/vue-query'
import { computed, ref, watch } from 'vue'
import { api } from '@/api/client'
import { useSession } from '@/app/session'
import Button from '@/shared/components/Button.vue'
import { table, typo } from '@/shared/components/components.styles'
import ConfirmDialog from '@/shared/components/ConfirmDialog.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import QueryView from '@/shared/components/QueryView.vue'
import { formatRelative } from '@/shared/format'
import Container from '@/shared/layout/Container.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Split from '@/shared/layout/Split.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import AliasDialog from '../components/AliasDialog.vue'
import CreatePolicyDialog from '../components/CreatePolicyDialog.vue'
import PolicyEditor from '../components/PolicyEditor.vue'
import VersionHistory from '../components/VersionHistory.vue'
import { routingKeys, usePolicies } from '../queries'

const session = useSession()
const queries = useQueryClient()
const policies = usePolicies()
const selectedId = ref('')
const selected = computed(
  () => policies.data.value?.items.find((p) => p.id === selectedId.value) ?? null,
)
watch(
  () => policies.data.value?.items,
  (items) => {
    if (!selectedId.value && items?.length) selectedId.value = items[0]!.id
  },
  { immediate: true },
)
const createOpen = ref(false)
const aliasOpen = ref(false)
const unbinding = ref<string | null>(null)
const unbindOpen = computed({
  get: () => unbinding.value !== null,
  set: (open) => !open && (unbinding.value = null),
})
const unbind = useMutation({
  mutationFn: async (alias: string) =>
    api.DELETE('/admin/aliases/{alias}', { params: { path: { alias } } }),
  onSuccess: async () => {
    unbinding.value = null
    await queries.invalidateQueries({ queryKey: routingKeys.all })
  },
})

const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono),
  caption: stylex.props(typo.caption),
}
const rowAttrs = (id: string) =>
  stylex.props(table.row, selectedId.value === id && table.highlighted)
</script>

<template>
  <Container>
    <PageHeader
      title="Routing"
      lede="Routing Policies decide which Provider Models may serve a Model Alias, and in what order. Every save is a new immutable version."
    >
      <template #actions>
        <Inline v-if="session.can('owner')" :gap="8">
          <Button @click="aliasOpen = true">Bind alias</Button>
          <Button variant="primary" @click="createOpen = true">Create policy</Button>
        </Inline>
      </template>
    </PageHeader>
    <QueryView
      :data="policies.data.value"
      :is-pending="policies.isPending.value"
      :error="policies.error.value"
      :refetch="policies.refetch"
      :is-empty="(page) => page.items.length === 0"
      empty-text="No Routing Policy yet. Create one, then bind a Model Alias to it so clients can use it."
    >
      <template #empty
        ><Button v-if="session.can('owner')" variant="primary" @click="createOpen = true"
          >Create policy</Button
        ></template
      >
      <template #default="{ data: page }">
        <Stack :gap="48">
          <Section title="Policies and aliases" surface flush>
            <TableScroll label="Routing Policies">
              <table v-bind="s.table">
                <thead>
                  <tr>
                    <th v-bind="s.th" scope="col">Policy</th>
                    <th v-bind="s.thNumber" scope="col">Version</th>
                    <th v-bind="s.th" scope="col">Strategy</th>
                    <th v-bind="s.th" scope="col">Model Aliases</th>
                    <th v-bind="s.th" scope="col">Saved</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="policy in page.items" :key="policy.id" v-bind="rowAttrs(policy.id)">
                    <td v-bind="s.td">
                      <Button
                        variant="ghost"
                        size="sm"
                        :aria-pressed="selectedId === policy.id"
                        @click="selectedId = policy.id"
                        ><span v-bind="s.mono">{{ policy.name }}</span></Button
                      >
                    </td>
                    <td v-bind="s.number">v{{ policy.version }}</td>
                    <td v-bind="s.td">
                      {{ (policy.definition as { strategy?: string } | null)?.strategy ?? '—' }}
                    </td>
                    <td v-bind="s.td">
                      <Inline :gap="4">
                        <span v-if="!policy.aliases.length" v-bind="s.caption"
                          >Not bound: clients cannot use it yet</span
                        >
                        <Inline
                          v-for="alias in policy.aliases"
                          :key="alias"
                          :gap="2"
                          :wrap="false"
                          as="span"
                        >
                          <span v-bind="s.mono">{{ alias }}</span>
                          <Button
                            v-if="session.can('owner')"
                            size="sm"
                            variant="ghost"
                            destructive
                            :aria-label="`Unbind ${alias}`"
                            @click="unbinding = alias"
                            >Unbind</Button
                          >
                        </Inline>
                      </Inline>
                    </td>
                    <td v-bind="s.td">{{ formatRelative(policy.versionCreatedAt) }}</td>
                  </tr>
                </tbody>
              </table>
            </TableScroll>
          </Section>
          <Split v-if="selected">
            <Section :title="`Edit ${selected.name}`">
              <PolicyEditor :policy="selected" :can-edit="session.can('owner')" />
            </Section>
            <template #aside>
              <Section title="Version history">
                <VersionHistory :policy-id="selected.id" />
              </Section>
            </template>
          </Split>
        </Stack>
        <AliasDialog v-model:visible="aliasOpen" :policies="page.items" :policy-id="selectedId" />
      </template>
    </QueryView>
    <CreatePolicyDialog v-model:visible="createOpen" />
    <ConfirmDialog
      v-model:visible="unbindOpen"
      :title="`Unbind ${unbinding}?`"
      action="Unbind alias"
      :pending="unbind.isPending.value"
      @confirm="unbinding && unbind.mutate(unbinding)"
    >
      <p>
        Requests for <code>{{ unbinding }}</code> will get <code>model_not_found</code> (404) until
        it is bound again.
      </p>
      <ErrorState v-if="unbind.error.value" :error="unbind.error.value" />
    </ConfirmDialog>
  </Container>
</template>
