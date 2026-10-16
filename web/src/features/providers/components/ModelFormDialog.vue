<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import { api, data, type Schemas } from '@/api/client'
import Button from '@/shared/components/Button.vue'
import Checkbox from '@/shared/components/Checkbox.vue'
import Dialog from '@/shared/components/Dialog.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { useFormMutation } from '@/shared/forms'
import Grid from '@/shared/layout/Grid.vue'
import Inline from '@/shared/layout/Inline.vue'
import Stack from '@/shared/layout/Stack.vue'
import { type CapabilityForm, fromForm, knownParameters, toForm } from '../capabilities'
import { providerKeys } from '../queries'

/** Add a Provider Model or edit its Capabilities (checkboxes and numeric fields, frontend.md section 2). */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ providerId: string; model?: Schemas['ProviderModelView'] | null }>()
const editing = computed(() => !!props.model)
const form = reactive({ providerModelId: '', displayName: '', enabled: true })
const capabilities = reactive<CapabilityForm>(toForm({}))

watch(visible, (open) => {
  if (!open) return
  form.providerModelId = props.model?.providerModelId ?? ''
  form.displayName = props.model?.displayName ?? ''
  form.enabled = props.model?.enabled ?? true
  Object.assign(
    capabilities,
    toForm(props.model?.capabilities ?? { streaming: true, parameters: knownParameters }),
  )
  save.reset()
})

const tokenizers = [
  { value: '', label: 'None (estimate by bytes)' },
  { value: 'o200k_base', label: 'o200k_base' },
  { value: 'cl100k_base', label: 'cl100k_base' },
]

const save = useFormMutation(
  async () => {
    const body = {
      displayName: form.displayName || null,
      capabilities: fromForm(capabilities) as never,
      enabled: form.enabled,
    }
    if (props.model)
      return data(
        await api.PATCH('/admin/models/{modelId}', {
          params: { path: { modelId: props.model.id } },
          body,
        }),
      )
    return data(
      await api.POST('/admin/providers/{providerId}/models', {
        params: { path: { providerId: props.providerId } },
        body: { ...body, providerModelId: form.providerModelId },
      }),
    )
  },
  [providerKeys.all],
  () => (visible.value = false),
)
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :title="editing ? `Edit ${model?.providerModelId}` : 'Add Provider Model'"
    wide
  >
    <form id="model-form" novalidate @submit.prevent="save.mutation.mutate(undefined)">
      <Stack :gap="12">
        <ErrorState v-if="save.generalError.value" :error="save.generalError.value" />
        <Field
          v-if="!editing"
          v-slot="{ id, invalid }"
          label="Provider's model id"
          help="Exactly as the Provider names it, e.g. gpt-4o-mini."
          :errors="save.errorsFor('providerModelId')"
        >
          <TextInput :id="id" v-model="form.providerModelId" mono :invalid="invalid" />
        </Field>
        <Field v-slot="{ id }" label="Display name" :errors="save.errorsFor('displayName')"
          ><TextInput :id="id" v-model="form.displayName"
        /></Field>
        <Inline :gap="16">
          <Checkbox v-model="capabilities.streaming" label="Streaming" />
          <Checkbox v-model="capabilities.jsonObject" label="JSON object output" />
          <Checkbox v-model="capabilities.jsonSchema" label="JSON-schema output" />
        </Inline>
        <Grid min="narrow" :gap="12">
          <Field
            v-slot="{ id, invalid }"
            label="Context window"
            :errors="save.errorsFor('capabilities.contextWindow')"
          >
            <TextInput
              :id="id"
              v-model="capabilities.contextWindow"
              type="number"
              :invalid="invalid"
            />
          </Field>
          <Field
            v-slot="{ id, invalid }"
            label="Max output tokens"
            :errors="save.errorsFor('capabilities.maxOutputTokens')"
          >
            <TextInput
              :id="id"
              v-model="capabilities.maxOutputTokens"
              type="number"
              :invalid="invalid"
            />
          </Field>
          <Field
            v-slot="{ id }"
            label="Tokenizer"
            :errors="save.errorsFor('capabilities.tokenizer')"
          >
            <SelectInput :id="id" v-model="capabilities.tokenizer" :options="tokenizers" />
          </Field>
        </Grid>
        <Field
          v-slot="{ id, invalid }"
          label="Supported parameters"
          help="Comma-separated: temperature, top_p, seed, stop, presence_penalty, frequency_penalty."
          :errors="save.errorsFor('capabilities.parameters')"
        >
          <TextInput :id="id" v-model="capabilities.parameters" mono :invalid="invalid" />
        </Field>
        <Checkbox v-model="form.enabled" label="Enabled" />
      </Stack>
    </form>
    <template #actions>
      <Button @click="visible = false">Cancel</Button>
      <Button
        type="submit"
        form="model-form"
        variant="primary"
        :loading="save.mutation.isPending.value"
        >{{ editing ? 'Save model' : 'Add model' }}</Button
      >
    </template>
  </Dialog>
</template>
