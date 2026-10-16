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
import Stack from '@/shared/layout/Stack.vue'
import { providerKeys } from '../queries'

/**
 * Create or edit a Provider. The secret is write-only: once saved it is never shown again, only "configured" (PRD section 9.3), and
 * leaving the field empty keeps the stored one.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ provider?: Schemas['ProviderView'] | null }>()
const editing = computed(() => !!props.provider)
const form = reactive({
  name: '',
  adapterType: 'openai_compatible',
  endpoint: '',
  requestTimeoutMs: '',
  enabled: true,
  credential: 'keep',
  secret: '',
  envVar: '',
})

watch(visible, (open) => {
  if (!open) return
  const p = props.provider
  Object.assign(form, {
    name: p?.name ?? '',
    adapterType: p?.adapterType ?? 'openai_compatible',
    endpoint: p?.endpoint ?? '',
    requestTimeoutMs: p ? String(p.requestTimeoutMs) : '',
    enabled: p?.enabled ?? true,
    credential: p ? 'keep' : 'none',
    secret: '',
    envVar: '',
  })
  save.reset()
})

const credentialOptions = computed(() => [
  ...(editing.value
    ? [
        {
          value: 'keep',
          label: `Keep the current credential (${props.provider?.credential.kind ?? 'none'})`,
        },
      ]
    : [{ value: 'none', label: 'No credential' }]),
  { value: 'secret', label: 'Store a secret (encrypted)' },
  { value: 'env', label: 'Read an environment variable' },
])
const endpointExample = 'https://api.example.com/v1'
const adapterOptions = [
  { value: 'openai_compatible', label: 'OpenAI-compatible' },
  { value: 'ollama', label: 'Ollama' },
]

const save = useFormMutation(
  async () => {
    const credential =
      form.credential === 'secret'
        ? { secret: form.secret, envVar: null }
        : form.credential === 'env'
          ? { secret: null, envVar: form.envVar }
          : null
    const timeout = form.requestTimeoutMs ? Number(form.requestTimeoutMs) : null
    if (props.provider) {
      return data(
        await api.PATCH('/admin/providers/{providerId}', {
          params: { path: { providerId: props.provider.id } },
          body: {
            endpoint: form.endpoint,
            requestTimeoutMs: timeout,
            enabled: form.enabled,
            credential,
          },
        }),
      )
    }
    return data(
      await api.POST('/admin/providers', {
        body: {
          name: form.name,
          adapterType: form.adapterType,
          endpoint: form.endpoint,
          requestTimeoutMs: timeout,
          enabled: form.enabled,
          credential,
        },
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
    :title="editing ? `Edit ${provider?.name}` : 'Add Provider'"
    wide
  >
    <form id="provider-form" novalidate @submit.prevent="save.mutation.mutate(undefined)">
      <Stack :gap="12">
        <ErrorState v-if="save.generalError.value" :error="save.generalError.value" />
        <Field
          v-if="!editing"
          v-slot="{ id, invalid }"
          label="Name"
          help="Lower case; Routing Policies name Candidates as name/model."
          :errors="save.errorsFor('name')"
        >
          <TextInput :id="id" v-model="form.name" mono :invalid="invalid" />
        </Field>
        <Field
          v-if="!editing"
          v-slot="{ id }"
          label="Adapter"
          :errors="save.errorsFor('adapterType')"
        >
          <SelectInput :id="id" v-model="form.adapterType" :options="adapterOptions" />
        </Field>
        <Field
          v-slot="{ id, invalid }"
          label="Endpoint"
          help="Use https; http only for a host on the private allow-list."
          :errors="save.errorsFor('endpoint')"
        >
          <TextInput
            :id="id"
            v-model="form.endpoint"
            mono
            :invalid="invalid"
            :placeholder="endpointExample"
          />
        </Field>
        <Field
          v-slot="{ id, invalid }"
          label="Request timeout (ms)"
          help="Empty keeps the default."
          :errors="save.errorsFor('requestTimeoutMs')"
        >
          <TextInput :id="id" v-model="form.requestTimeoutMs" type="number" :invalid="invalid" />
        </Field>
        <Field v-slot="{ id }" label="Credential" :errors="save.errorsFor('credential')">
          <SelectInput :id="id" v-model="form.credential" :options="credentialOptions" />
        </Field>
        <Field
          v-if="form.credential === 'secret'"
          v-slot="{ id, invalid }"
          label="Secret"
          help="Write-only: it is encrypted at rest and never shown again."
          :errors="save.errorsFor('credential.secret')"
        >
          <TextInput
            :id="id"
            v-model="form.secret"
            type="password"
            autocomplete="off"
            mono
            :invalid="invalid"
          />
        </Field>
        <Field
          v-if="form.credential === 'env'"
          v-slot="{ id, invalid }"
          label="Environment variable"
          help="Its name, e.g. OPENAI_API_KEY. MODELSWITCH__ names are refused."
          :errors="save.errorsFor('credential.envVar')"
        >
          <TextInput :id="id" v-model="form.envVar" mono :invalid="invalid" />
        </Field>
        <Checkbox v-model="form.enabled" label="Enabled" />
      </Stack>
    </form>
    <template #actions>
      <Button @click="visible = false">Cancel</Button>
      <Button
        type="submit"
        form="provider-form"
        variant="primary"
        :loading="save.mutation.isPending.value"
        >{{ editing ? 'Save Provider' : 'Add Provider' }}</Button
      >
    </template>
  </Dialog>
</template>
