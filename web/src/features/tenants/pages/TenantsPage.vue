<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { reactive, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { api, data } from '@/api/client'
import { useSession } from '@/app/session'
import Button from '@/shared/components/Button.vue'
import { link, table, typo } from '@/shared/components/components.styles'
import Dialog from '@/shared/components/Dialog.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import QueryView from '@/shared/components/QueryView.vue'
import StatusBadge from '@/shared/components/StatusBadge.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { formatTimestamp } from '@/shared/format'
import { useFormMutation } from '@/shared/forms'
import Container from '@/shared/layout/Container.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { tenantKeys, useTenants } from '../queries'

const session = useSession()
const tenants = useTenants()
const createOpen = ref(false)
const form = reactive({ name: '' })
watch(createOpen, (open) => {
  if (!open) return
  form.name = ''
  create.reset()
})
const create = useFormMutation(
  async () => data(await api.POST('/admin/tenants', { body: { name: form.name } })),
  [tenantKeys.all],
  () => (createOpen.value = false),
)
const canCreate = () => session.can('operator') && session.administrator?.allTenants === true
const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  td: stylex.props(table.td),
  name: stylex.props(link.quiet, link.focus),
  mono: stylex.props(typo.mono),
}
</script>

<template>
  <Container>
    <PageHeader
      title="Tenants"
      lede="The customers of the gateway: each one's limits, Budget, model access and API Keys."
    >
      <template #actions
        ><Button v-if="canCreate()" variant="primary" @click="createOpen = true"
          >Create Tenant</Button
        ></template
      >
    </PageHeader>
    <Section title="Tenants" surface flush>
      <QueryView
        :data="tenants.data.value"
        :is-pending="tenants.isPending.value"
        :error="tenants.error.value"
        :refetch="tenants.refetch"
        :is-empty="(page) => page.items.length === 0"
        empty-text="No Tenant you can see yet. Create one, then give it an API Key."
      >
        <template #default="{ data: page }">
          <TableScroll label="Tenants">
            <table v-bind="s.table">
              <thead>
                <tr>
                  <th v-bind="s.th" scope="col">Name</th>
                  <th v-bind="s.th" scope="col">Status</th>
                  <th v-bind="s.th" scope="col">Id</th>
                  <th v-bind="s.th" scope="col">Created</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="tenant in page.items" :key="tenant.id">
                  <td v-bind="s.td">
                    <RouterLink :to="`/tenants/${tenant.id}`" v-bind="s.name">{{
                      tenant.name
                    }}</RouterLink>
                  </td>
                  <td v-bind="s.td"><StatusBadge :status="tenant.status" /></td>
                  <td v-bind="s.td">
                    <span v-bind="s.mono">{{ tenant.id }}</span>
                  </td>
                  <td v-bind="s.td">{{ formatTimestamp(tenant.createdAt) }}</td>
                </tr>
              </tbody>
            </table>
          </TableScroll>
        </template>
      </QueryView>
    </Section>
    <Dialog v-model:visible="createOpen" title="Create Tenant">
      <form id="tenant-form" novalidate @submit.prevent="create.mutation.mutate(undefined)">
        <Stack :gap="12">
          <ErrorState v-if="create.generalError.value" :error="create.generalError.value" />
          <Field v-slot="{ id, invalid }" label="Name" :errors="create.errorsFor('name')"
            ><TextInput :id="id" v-model="form.name" :invalid="invalid"
          /></Field>
        </Stack>
      </form>
      <template #actions>
        <Button @click="createOpen = false">Cancel</Button>
        <Button
          type="submit"
          form="tenant-form"
          variant="primary"
          :loading="create.mutation.isPending.value"
          >Create Tenant</Button
        >
      </template>
    </Dialog>
  </Container>
</template>
