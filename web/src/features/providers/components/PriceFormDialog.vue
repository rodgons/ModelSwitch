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
import { providerKeys } from '../queries'

/** Prices are immutable and versioned: adding one appends a new version from its effective time (usage-and-budgets.md section 1). */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ modelId: string; modelName: string }>()
const form = reactive({
  inputPerMillion: '',
  cachedInputPerMillion: '',
  outputPerMillion: '',
  effectiveFrom: '',
})
watch(visible, (open) => {
  if (!open) return
  Object.assign(form, {
    inputPerMillion: '',
    cachedInputPerMillion: '',
    outputPerMillion: '',
    effectiveFrom: '',
  })
  save.reset()
})

const number = (value: string) => (value.trim() ? Number(value) : null)
const save = useFormMutation(
  async () =>
    data(
      await api.POST('/admin/models/{modelId}/prices', {
        params: { path: { modelId: props.modelId } },
        body: {
          inputPerMillion: number(form.inputPerMillion),
          cachedInputPerMillion: number(form.cachedInputPerMillion),
          outputPerMillion: number(form.outputPerMillion),
          currency: 'USD',
          effectiveFrom: form.effectiveFrom
            ? new Date(`${form.effectiveFrom}Z`).toISOString()
            : null,
        },
      }),
    ),
  [providerKeys.all],
  () => (visible.value = false),
)
</script>

<template>
  <Dialog v-model:visible="visible" :title="`Add price for ${modelName}`">
    <form id="price-form" novalidate @submit.prevent="save.mutation.mutate(undefined)">
      <Stack :gap="12">
        <ErrorState v-if="save.generalError.value" :error="save.generalError.value" />
        <Field
          v-slot="{ id, invalid }"
          label="Input, USD per 1M tokens"
          :errors="save.errorsFor('inputPerMillion')"
          ><TextInput
            :id="id"
            v-model="form.inputPerMillion"
            inputmode="decimal"
            :invalid="invalid"
        /></Field>
        <Field
          v-slot="{ id, invalid }"
          label="Cached input, USD per 1M tokens"
          help="Empty: cached tokens cost the input rate."
          :errors="save.errorsFor('cachedInputPerMillion')"
        >
          <TextInput
            :id="id"
            v-model="form.cachedInputPerMillion"
            inputmode="decimal"
            :invalid="invalid"
          />
        </Field>
        <Field
          v-slot="{ id, invalid }"
          label="Output, USD per 1M tokens"
          :errors="save.errorsFor('outputPerMillion')"
          ><TextInput
            :id="id"
            v-model="form.outputPerMillion"
            inputmode="decimal"
            :invalid="invalid"
        /></Field>
        <Field
          v-slot="{ id, invalid }"
          label="Effective from (UTC)"
          help="Empty: now. Existing Prices never change."
          :errors="save.errorsFor('effectiveFrom')"
        >
          <TextInput
            :id="id"
            v-model="form.effectiveFrom"
            type="datetime-local"
            :invalid="invalid"
          />
        </Field>
      </Stack>
    </form>
    <template #actions>
      <Button @click="visible = false">Cancel</Button>
      <Button
        type="submit"
        form="price-form"
        variant="primary"
        :loading="save.mutation.isPending.value"
        >Add price</Button
      >
    </template>
  </Dialog>
</template>
