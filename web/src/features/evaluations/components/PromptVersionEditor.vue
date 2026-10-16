<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, ref, watch } from 'vue'
import { api, ApiError, data, type Schemas } from '@/api/client'
import Alert from '@/shared/components/Alert.vue'
import Badge from '@/shared/components/Badge.vue'
import Button from '@/shared/components/Button.vue'
import { typo } from '@/shared/components/components.styles'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { useFormMutation } from '@/shared/forms'
import Inline from '@/shared/layout/Inline.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import { evaluationKeys } from '../queries'
import {
  detectVariables,
  moveItem,
  parseResponseFormat,
  readTemplateErrors,
  roles,
  type TemplateMessage,
} from '../template'

/**
 * Writes the next immutable Prompt Version (evaluation.md section 2): ordered messages with `{{variable}}` placeholders and an
 * optional response format. It starts from the version shown, so an edit is a copy, never a change in place.
 */
const props = defineProps<{ promptId: string; from: Schemas['PromptVersionView'] | null }>()
const emit = defineEmits<{ saved: [version: number]; cancel: [] }>()

interface Draft extends TemplateMessage {
  key: number
}
let nextKey = 0
const draft = (message: TemplateMessage): Draft => ({ ...message, key: nextKey++ })
const messages = ref<Draft[]>([])
const responseFormat = ref('')

const roleOptions = roles.map((role) => ({
  value: role,
  label: role.charAt(0).toUpperCase() + role.slice(1),
}))
const variables = computed(() => detectVariables(messages.value))
const parsedFormat = computed(() => parseResponseFormat(responseFormat.value))

const add = () => (messages.value = [...messages.value, draft({ role: 'user', content: '' })])
const remove = (index: number) => (messages.value = messages.value.filter((_, i) => i !== index))
const move = (index: number, delta: number) =>
  (messages.value = moveItem(messages.value, index, delta))

const save = useFormMutation(
  async () => {
    const format = parsedFormat.value
    if (!format.ok) throw new Error(`Response format: ${format.error}`)
    return data(
      await api.POST('/admin/prompts/{promptId}/versions', {
        params: { path: { promptId: props.promptId } },
        body: {
          messages: messages.value.map(({ role, content }) => ({ role, content })),
          responseFormat: format.value as never,
        },
      }),
    )
  },
  [evaluationKeys.prompts],
  (created) => emit('saved', created.version),
)
// Keyed on the version number, not the object: a background refetch must not wipe what is being typed.
watch(
  () => [props.promptId, props.from?.version] as const,
  () => {
    const from = props.from
    messages.value = from
      ? from.template.messages.map(draft)
      : [draft({ role: 'system', content: '' }), draft({ role: 'user', content: '{{text}}' })]
    responseFormat.value = from?.template.responseFormat
      ? JSON.stringify(from.template.responseFormat, null, 2)
      : ''
    save.reset()
  },
  { immediate: true },
)
const errors = computed(() =>
  readTemplateErrors(save.failure.value instanceof ApiError ? save.failure.value.fieldErrors : {}),
)
const formatErrors = computed(() =>
  parsedFormat.value.ok ? errors.value.responseFormat : [parsedFormat.value.error],
)
const s = { caption: stylex.props(typo.caption), mono: stylex.props(typo.mono) }
</script>

<template>
  <Section :title="from ? `New version from v${from.version}` : 'First version'" surface>
    <form novalidate @submit.prevent="save.mutation.mutate(undefined)">
      <Stack :gap="16">
        <ErrorState v-if="save.generalError.value" :error="save.generalError.value" />
        <Alert v-if="errors.general.length" tone="danger">
          <span
            >The template was rejected (<code>validation_failed</code>).
            {{ errors.general.join(' ') }}</span
          >
        </Alert>
        <Stack
          v-for="(message, index) in messages"
          :key="message.key"
          role="group"
          :gap="8"
          :aria-label="`Message ${index + 1}`"
        >
          <Inline justify="between" align="end" :gap="8">
            <Field
              v-slot="{ id }"
              :label="`Message ${index + 1} role`"
              :errors="errors.message(index).role"
              ><SelectInput :id="id" v-model="message.role" :options="roleOptions"
            /></Field>
            <Inline :gap="4">
              <Button
                variant="ghost"
                size="sm"
                :disabled="index === 0"
                :aria-label="`Move message ${index + 1} up`"
                @click="move(index, -1)"
                >Up</Button
              >
              <Button
                variant="ghost"
                size="sm"
                :disabled="index === messages.length - 1"
                :aria-label="`Move message ${index + 1} down`"
                @click="move(index, 1)"
                >Down</Button
              >
              <Button
                variant="ghost"
                size="sm"
                destructive
                :disabled="messages.length === 1"
                :aria-label="`Remove message ${index + 1}`"
                @click="remove(index)"
                >Remove</Button
              >
            </Inline>
          </Inline>
          <Field
            v-slot="{ id, invalid }"
            :label="`Message ${index + 1} content`"
            :errors="errors.message(index).content"
            ><TextInput :id="id" v-model="message.content" multiline mono :invalid="invalid"
          /></Field>
        </Stack>
        <div><Button @click="add">Add message</Button></div>
        <Stack :gap="6">
          <span v-bind="s.caption">Variables each Case input must have</span>
          <Inline v-if="variables.length" :gap="6">
            <Badge v-for="name in variables" :key="name" tone="accent" :dot="false"
              ><span v-bind="s.mono">{{ name }}</span></Badge
            >
          </Inline>
          <span v-else v-bind="s.caption"
            >None. Write <code v-pre>{{ name }}</code> in a message to insert a Case input
            value.</span
          >
        </Stack>
        <Field
          v-slot="{ id, invalid }"
          label="Response format (optional JSON)"
          help='{"type":"text"}, {"type":"json_object"} or {"type":"json_schema","schema":{…}}. Empty: none.'
          :errors="formatErrors"
          ><TextInput :id="id" v-model="responseFormat" multiline mono :invalid="invalid"
        /></Field>
        <Inline :gap="8">
          <Button
            type="submit"
            variant="primary"
            :disabled="!parsedFormat.ok"
            :loading="save.mutation.isPending.value"
            >Create version</Button
          >
          <Button @click="emit('cancel')">Cancel</Button>
        </Inline>
      </Stack>
    </form>
  </Section>
</template>
