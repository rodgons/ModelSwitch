<script setup lang="ts">
import { reactive, watch } from 'vue'
import { api, data } from '@/api/client'
import Button from '@/shared/components/Button.vue'
import Dialog from '@/shared/components/Dialog.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { useFormMutation } from '@/shared/forms'
import Stack from '@/shared/layout/Stack.vue'
import { routingKeys } from '../queries'

const visible = defineModel<boolean>('visible', { required: true })
const form = reactive({ name: '', candidates: '' })
watch(visible, (open) => {
  if (!open) return
  Object.assign(form, { name: '', candidates: '' })
  save.reset()
})

const save = useFormMutation(
  async () =>
    data(
      await api.POST('/admin/routing-policies', {
        body: {
          name: form.name,
          definition: {
            strategy: 'static',
            candidates: form.candidates
              .split('\n')
              .map((c) => c.trim())
              .filter(Boolean),
          } as never,
        },
      }),
    ),
  [routingKeys.all],
  () => (visible.value = false),
)
</script>

<template>
  <Dialog v-model:visible="visible" title="Create Routing Policy" wide>
    <form id="policy-form" novalidate @submit.prevent="save.mutation.mutate(undefined)">
      <Stack :gap="12">
        <ErrorState v-if="save.generalError.value" :error="save.generalError.value" />
        <Field v-slot="{ id, invalid }" label="Name" :errors="save.errorsFor('name')"
          ><TextInput :id="id" v-model="form.name" mono :invalid="invalid"
        /></Field>
        <Field
          v-slot="{ id, invalid }"
          label="Candidates"
          help="One provider/model per line, in order. Rules and execution settings can be added after it is created."
          :errors="save.errorsFor('definition.candidates')"
        >
          <TextInput :id="id" v-model="form.candidates" multiline mono :invalid="invalid" />
        </Field>
      </Stack>
    </form>
    <template #actions>
      <Button @click="visible = false">Cancel</Button>
      <Button
        type="submit"
        form="policy-form"
        variant="primary"
        :loading="save.mutation.isPending.value"
        >Create policy</Button
      >
    </template>
  </Dialog>
</template>
