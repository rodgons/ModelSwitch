<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { api, ApiError, data, type Schemas } from '@/api/client'
import Alert from '@/shared/components/Alert.vue'
import Button from '@/shared/components/Button.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import SegmentedControl from '@/shared/components/SegmentedControl.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { useFormMutation } from '@/shared/forms'
import Grid from '@/shared/layout/Grid.vue'
import Inline from '@/shared/layout/Inline.vue'
import Stack from '@/shared/layout/Stack.vue'
import { pretty } from '../diff'
import { type PolicyForm, readForm, writeForm } from '../policyForm'
import { routingKeys } from '../queries'

/**
 * Edits a Routing Policy. Saving creates version n+1 (FR-11), which aliases resolve to at once; it carries the version it was edited
 * from, so a concurrent edit answers 409 instead of being overwritten. Validation is a dry run of the same checks.
 */
const props = defineProps<{ policy: Schemas['PolicyView']; canEdit: boolean }>()
const view = ref<'form' | 'json'>('form')
const json = ref('')
const form = reactive<PolicyForm>(readForm({}))
const parseError = ref<string | null>(null)
const validation = ref<Schemas['PolicyValidationView'] | ApiError | null>(null)

function load() {
  json.value = pretty(props.policy.definition)
  Object.assign(form, readForm(props.policy.definition))
  validation.value = null
  parseError.value = null
}
watch(() => [props.policy.id, props.policy.version], load, { immediate: true })

// The two views edit one definition: switching writes the visible one into the other.
watch(view, (now) => {
  if (now === 'json') json.value = pretty(writeForm(parsed() ?? {}, form))
  else if (parsed()) Object.assign(form, readForm(parsed()))
})

function parsed(): unknown {
  try {
    parseError.value = null
    return JSON.parse(json.value)
  } catch {
    parseError.value = 'The definition is not valid JSON.'
    return null
  }
}

const definition = () => (view.value === 'form' ? writeForm(parsed() ?? {}, form) : parsed())

async function validate() {
  const value = definition()
  if (value === null) return
  try {
    validation.value = data(
      await api.POST('/admin/routing-policies/validate', { body: { definition: value as never } }),
    )
  } catch (error) {
    validation.value = error instanceof ApiError ? error : null
  }
}

const save = useFormMutation(async () => {
  const value = definition()
  if (value === null) throw new Error('The definition is not valid JSON.')
  return data(
    await api.PUT('/admin/routing-policies/{policyId}', {
      params: { path: { policyId: props.policy.id } },
      body: { definition: value as never, expectedVersion: props.policy.version },
    }),
  )
}, [routingKeys.all])

const problems = computed(() => {
  const failure =
    validation.value instanceof ApiError
      ? validation.value
      : save.failure.value instanceof ApiError
        ? save.failure.value
        : null
  return failure
    ? Object.entries(failure.fieldErrors).map(
        ([field, messages]) => `${field}: ${messages.join(' ')}`,
      )
    : []
})
const strategies = [
  { value: 'static', label: 'Static (in order)' },
  { value: 'rules', label: 'Rules' },
  { value: 'cost', label: 'Lowest worst-case cost' },
]
</script>

<template>
  <Stack :gap="16">
    <Inline justify="between">
      <SegmentedControl
        v-model="view"
        label="Editor view"
        :options="[
          { value: 'form', label: 'Form' },
          { value: 'json', label: 'JSON' },
        ]"
      />
      <Inline v-if="canEdit" :gap="8">
        <Button @click="validate">Validate</Button>
        <Button
          variant="primary"
          :loading="save.mutation.isPending.value"
          @click="save.mutation.mutate(undefined)"
          >Save as version {{ policy.version + 1 }}</Button
        >
      </Inline>
    </Inline>
    <Alert v-if="parseError" tone="danger">{{ parseError }}</Alert>
    <Alert v-if="save.mutation.isSuccess.value" tone="success"
      >Saved. Aliases bound to {{ policy.name }} now resolve to the new version.</Alert
    >
    <Alert
      v-if="save.failure.value instanceof ApiError && save.failure.value.errorCode === 'conflict'"
      tone="warning"
    >
      Someone saved a newer version while you were editing. Reload the policy, then apply your
      change again.
    </Alert>
    <ErrorState v-else-if="save.generalError.value" :error="save.generalError.value" />
    <Alert
      v-if="validation && !(validation instanceof ApiError) && validation.valid"
      tone="success"
    >
      Valid. Candidates resolve to
      {{ Object.keys(validation.resolvedCandidates).join(', ') || 'no model' }}.
    </Alert>
    <Alert v-if="problems.length" tone="danger">
      <span>The definition was rejected (<code>validation_failed</code>):</span>
      <ul>
        <li v-for="problem in problems" :key="problem">{{ problem }}</li>
      </ul>
    </Alert>
    <template v-if="view === 'form'">
      <Grid min="field" :gap="12">
        <Field v-slot="{ id }" label="Strategy"
          ><SelectInput :id="id" v-model="form.strategy" :options="strategies"
        /></Field>
        <Field v-slot="{ id }" label="Deadline (ms)"
          ><TextInput :id="id" v-model="form.deadlineMs" type="number"
        /></Field>
        <Field v-slot="{ id }" label="Attempt timeout (ms)"
          ><TextInput :id="id" v-model="form.attemptTimeoutMs" type="number"
        /></Field>
        <Field v-slot="{ id }" label="Max Attempts"
          ><TextInput :id="id" v-model="form.maxAttempts" type="number"
        /></Field>
        <Field v-slot="{ id }" label="Max Estimated Cost per request (USD)"
          ><TextInput :id="id" v-model="form.maxRequestCost" inputmode="decimal"
        /></Field>
      </Grid>
      <Field
        v-slot="{ id }"
        label="Candidates"
        help="One provider/model per line, in order. Rules and other settings are edited in the JSON view."
      >
        <TextInput :id="id" v-model="form.candidates" multiline mono />
      </Field>
    </template>
    <Field
      v-else
      v-slot="{ id }"
      label="Definition (JSON)"
      help="routing-and-resilience.md section 2 describes every key."
    >
      <TextInput :id="id" v-model="json" multiline mono />
    </Field>
  </Stack>
</template>
