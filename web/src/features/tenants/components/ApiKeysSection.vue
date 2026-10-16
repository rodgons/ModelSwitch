<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { useMutation, useQueryClient } from '@tanstack/vue-query'
import { computed, reactive, ref, watch } from 'vue'
import { api, data, type Schemas } from '@/api/client'
import Alert from '@/shared/components/Alert.vue'
import Badge from '@/shared/components/Badge.vue'
import Button from '@/shared/components/Button.vue'
import Checkbox from '@/shared/components/Checkbox.vue'
import { table, typo } from '@/shared/components/components.styles'
import ConfirmDialog from '@/shared/components/ConfirmDialog.vue'
import Dialog from '@/shared/components/Dialog.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import Identifier from '@/shared/components/Identifier.vue'
import QueryView from '@/shared/components/QueryView.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { formatRelative, formatTimestamp } from '@/shared/format'
import { useFormMutation } from '@/shared/forms'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { tenantKeys, useApiKeys } from '../queries'

/**
 * A Tenant's API Keys. A new key is shown exactly once, with a copy button, and the dialog stays until the Administrator confirms they
 * saved it. Revoking asks for the Key Prefix to be typed, because applications using the key start failing at once.
 */
const props = defineProps<{ tenantId: string; canEdit: boolean }>()
const queries = useQueryClient()
const keys = useApiKeys(() => props.tenantId)
const createOpen = ref(false)
const created = ref<Schemas['CreatedApiKeyView'] | null>(null)
const saved = ref(false)
const form = reactive({ name: '', expiresAt: '' })
watch(createOpen, (open) => {
  if (!open) return
  Object.assign(form, { name: '', expiresAt: '' })
  create.reset()
})
const create = useFormMutation(
  async () =>
    data(
      await api.POST('/admin/tenants/{tenantId}/api-keys', {
        params: { path: { tenantId: props.tenantId } },
        body: {
          name: form.name,
          scopes: null,
          expiresAt: form.expiresAt ? new Date(`${form.expiresAt}Z`).toISOString() : null,
        },
      }),
    ),
  [tenantKeys.keys(props.tenantId)],
  (result) => {
    createOpen.value = false
    saved.value = false
    created.value = result
  },
)
const createdOpen = computed({
  get: () => created.value !== null,
  set: (open) => !open && saved.value && (created.value = null),
})

const revoking = ref<Schemas['ApiKeyView'] | null>(null)
const revokeOpen = computed({
  get: () => revoking.value !== null,
  set: (open) => !open && (revoking.value = null),
})
const revoke = useMutation({
  mutationFn: async (keyId: string) =>
    api.DELETE('/admin/tenants/{tenantId}/api-keys/{keyId}', {
      params: { path: { tenantId: props.tenantId, keyId } },
    }),
  onSuccess: async () => {
    revoking.value = null
    await queries.invalidateQueries({ queryKey: tenantKeys.keys(props.tenantId) })
  },
})
const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  td: stylex.props(table.td),
  mono: stylex.props(typo.mono),
  caption: stylex.props(typo.caption),
}
</script>

<template>
  <Section title="API Keys" surface flush>
    <template #actions
      ><Button v-if="canEdit" size="sm" @click="createOpen = true">Create key</Button></template
    >
    <QueryView
      :data="keys.data.value"
      :is-pending="keys.isPending.value"
      :error="keys.error.value"
      :refetch="keys.refetch"
      :is-empty="(page) => page.items.length === 0"
      empty-text="No API Key yet. Create one so an application can call the gateway."
    >
      <template #default="{ data: page }">
        <TableScroll label="API Keys">
          <table v-bind="s.table">
            <thead>
              <tr>
                <th v-bind="s.th" scope="col">Key Prefix</th>
                <th v-bind="s.th" scope="col">Name</th>
                <th v-bind="s.th" scope="col">Scopes</th>
                <th v-bind="s.th" scope="col">Last used</th>
                <th v-bind="s.th" scope="col">State</th>
                <th v-bind="s.th" scope="col"><span v-bind="s.caption">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="key in page.items" :key="key.id">
                <td v-bind="s.td">
                  <span v-bind="s.mono">{{ key.keyPrefix }}</span>
                </td>
                <td v-bind="s.td">{{ key.name }}</td>
                <td v-bind="s.td">
                  <span v-bind="s.mono">{{ key.scopes.join(', ') }}</span>
                </td>
                <td v-bind="s.td">
                  {{ key.lastUsedAt ? formatRelative(key.lastUsedAt) : 'Never' }}
                </td>
                <td v-bind="s.td">
                  <Badge v-if="key.revokedAt" tone="danger">Revoked</Badge>
                  <Badge
                    v-else-if="key.expiresAt && key.expiresAt < new Date().toISOString()"
                    tone="warning"
                    >Expired</Badge
                  >
                  <Badge
                    v-else
                    tone="success"
                    :title="
                      key.expiresAt
                        ? `Expires ${formatTimestamp(key.expiresAt)}`
                        : 'Does not expire'
                    "
                    >Active</Badge
                  >
                </td>
                <td v-bind="s.td">
                  <Button
                    v-if="canEdit && !key.revokedAt"
                    size="sm"
                    destructive
                    @click="revoking = key"
                    >Revoke key</Button
                  >
                </td>
              </tr>
            </tbody>
          </table>
        </TableScroll>
      </template>
    </QueryView>

    <Dialog v-model:visible="createOpen" title="Create API Key">
      <form id="key-form" novalidate @submit.prevent="create.mutation.mutate(undefined)">
        <Stack :gap="12">
          <ErrorState v-if="create.generalError.value" :error="create.generalError.value" />
          <Field
            v-slot="{ id, invalid }"
            label="Name"
            help="Which application uses it."
            :errors="create.errorsFor('name')"
            ><TextInput :id="id" v-model="form.name" :invalid="invalid"
          /></Field>
          <Field
            v-slot="{ id, invalid }"
            label="Expires (UTC)"
            help="Empty: the key does not expire."
            :errors="create.errorsFor('expiresAt')"
          >
            <TextInput :id="id" v-model="form.expiresAt" type="datetime-local" :invalid="invalid" />
          </Field>
        </Stack>
      </form>
      <template #actions>
        <Button @click="createOpen = false">Cancel</Button>
        <Button
          type="submit"
          form="key-form"
          variant="primary"
          :loading="create.mutation.isPending.value"
          >Create key</Button
        >
      </template>
    </Dialog>

    <Dialog v-model:visible="createdOpen" title="Copy the new key now" :dismissable="false">
      <Stack :gap="16">
        <Alert tone="warning"
          >This is the only time the key is shown. The gateway keeps only a hash of it.</Alert
        >
        <Identifier v-if="created" :value="created.key" :head="created.key.length" :tail="0" copy />
        <Checkbox v-model="saved" label="I've saved the key somewhere safe" />
      </Stack>
      <template #actions>
        <Button variant="primary" :disabled="!saved" @click="created = null">Done</Button>
      </template>
    </Dialog>

    <ConfirmDialog
      v-model:visible="revokeOpen"
      :title="`Revoke key ${revoking?.keyPrefix}?`"
      action="Revoke key"
      :confirm-text="revoking?.keyPrefix"
      :pending="revoke.isPending.value"
      @confirm="revoking && revoke.mutate(revoking.id)"
    >
      <p>
        Applications that use it will get 401 errors immediately on this instance, and within 30
        seconds on the others.
      </p>
      <ErrorState v-if="revoke.error.value" :error="revoke.error.value" />
    </ConfirmDialog>
  </Section>
</template>
