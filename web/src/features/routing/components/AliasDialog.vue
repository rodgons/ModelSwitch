<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import { api, data, type Schemas } from '@/api/client'
import Button from '@/shared/components/Button.vue'
import Dialog from '@/shared/components/Dialog.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { useFormMutation } from '@/shared/forms'
import Stack from '@/shared/layout/Stack.vue'
import { routingKeys } from '../queries'

/** Binds a Model Alias to a Routing Policy; binding an existing alias moves it (rebind). */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ policies: Schemas['PolicyView'][]; policyId?: string }>()
const form = reactive({ alias: '', policyId: '', description: '' })
watch(visible, (open) => {
  if (!open) return
  Object.assign(form, {
    alias: '',
    policyId: props.policyId ?? props.policies[0]?.id ?? '',
    description: '',
  })
  save.reset()
})
const options = computed(() => props.policies.map((p) => ({ value: p.id, label: p.name })))

const save = useFormMutation(
  async () =>
    data(
      await api.PUT('/admin/aliases/{alias}', {
        params: { path: { alias: form.alias } },
        body: { policyId: form.policyId, description: form.description || null },
      }),
    ),
  [routingKeys.all],
  () => (visible.value = false),
)
</script>

<template>
  <Dialog v-model:visible="visible" title="Bind Model Alias">
    <form id="alias-form" novalidate @submit.prevent="save.mutation.mutate(undefined)">
      <Stack :gap="12">
        <ErrorState v-if="save.generalError.value" :error="save.generalError.value" />
        <Field
          v-slot="{ id, invalid }"
          label="Model Alias"
          help="What clients send as model, e.g. balanced."
          :errors="save.errorsFor('alias')"
          ><TextInput :id="id" v-model="form.alias" mono :invalid="invalid"
        /></Field>
        <Field v-slot="{ id }" label="Routing Policy" :errors="save.errorsFor('policyId')"
          ><SelectInput :id="id" v-model="form.policyId" :options="options"
        /></Field>
        <Field v-slot="{ id }" label="Description" :errors="save.errorsFor('description')"
          ><TextInput :id="id" v-model="form.description"
        /></Field>
      </Stack>
    </form>
    <template #actions>
      <Button @click="visible = false">Cancel</Button>
      <Button
        type="submit"
        form="alias-form"
        variant="primary"
        :loading="save.mutation.isPending.value"
        >Bind alias</Button
      >
    </template>
  </Dialog>
</template>
