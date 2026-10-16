<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { useMutation, useQueryClient } from '@tanstack/vue-query'
import { computed, ref, useId } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import Alert from '@/shared/components/Alert.vue'
import Button from '@/shared/components/Button.vue'
import { field, typo } from '@/shared/components/components.styles'
import ErrorState from '@/shared/components/ErrorState.vue'
import Stack from '@/shared/layout/Stack.vue'
import { upload } from '../evaluations.styles'
import { evaluationKeys, uploadDatasetVersion } from '../queries'
import { checkUploadFile, readUploadErrors } from '../upload'

/**
 * Uploads a JSONL file as the next Dataset Version. The size and type are checked here before sending; a rejected upload lists the
 * server's errors by line number (evaluation.md section 1: one bad line rejects the whole file).
 */
const props = defineProps<{ datasetId: string; taskKind: string }>()
const router = useRouter()
const queries = useQueryClient()
const id = useId()
const input = ref<HTMLInputElement | null>(null)
const file = ref<File | null>(null)
const localError = ref<string | null>(null)

function choose(event: Event) {
  const chosen = (event.target as HTMLInputElement).files?.[0] ?? null
  file.value = chosen
  localError.value = chosen ? checkUploadFile(chosen) : null
  send.reset()
}

const send = useMutation({
  mutationFn: (chosen: File) => uploadDatasetVersion(props.datasetId, chosen),
  onSuccess: async (created) => {
    await queries.invalidateQueries({ queryKey: evaluationKeys.datasets })
    file.value = null
    if (input.value) input.value.value = ''
    await router.push(`/evaluations/datasets/${props.datasetId}/versions/${created.version}`)
  },
})
function submit() {
  if (!file.value) {
    localError.value = 'Choose a .jsonl file first.'
    return
  }
  localError.value = checkUploadFile(file.value)
  if (!localError.value) send.mutate(file.value)
}

const rejected = computed(() => {
  const error = send.error.value
  return error instanceof ApiError && Object.keys(error.fieldErrors).length
    ? readUploadErrors(error.fieldErrors)
    : null
})
const expectedHelp = computed(
  () =>
    ({
      extraction: 'In this Dataset, expected is a non-empty object of fields.',
      classification: 'In this Dataset, expected is a label string.',
      free_text: 'In this Dataset, expected is a reference answer string.',
    })[props.taskKind] ?? '',
)
const s = {
  label: stylex.props(field.label),
  help: stylex.props(field.help),
  file: stylex.props(upload.file),
  list: stylex.props(upload.list),
  line: stylex.props(upload.line, typo.mono),
}
</script>

<template>
  <form novalidate @submit.prevent="submit">
    <Stack :gap="12">
      <Stack :gap="6">
        <label :for="id" v-bind="s.label">JSONL file</label>
        <input
          :id="id"
          ref="input"
          type="file"
          accept=".jsonl,.ndjson,application/x-ndjson"
          v-bind="s.file"
          :aria-describedby="`${id}-help`"
          :aria-invalid="localError ? true : undefined"
          @change="choose"
        />
        <p :id="`${id}-help`" v-bind="s.help">
          One Case per line: <code>id</code>, <code>input</code> (an object of template variables),
          <code>expected</code> and optional <code>metadata</code>. {{ expectedHelp }} Up to 5,000
          Cases and 10 MiB.
        </p>
      </Stack>
      <Alert v-if="localError" tone="danger">{{ localError }}</Alert>
      <Alert v-else-if="rejected" tone="danger">
        <span
          >The upload was rejected (<code>validation_failed</code>). Nothing was saved: fix these
          lines and upload the file again.</span
        >
        <ul v-if="rejected.general.length" v-bind="s.list">
          <li v-for="message in rejected.general" :key="message">{{ message }}</li>
        </ul>
        <ul v-if="rejected.lines.length" v-bind="s.list" aria-label="Errors by line">
          <li v-for="entry in rejected.lines" :key="entry.line">
            <span v-bind="s.line">Line {{ entry.line }}</span
            >:
            <template v-for="(problem, index) in entry.problems" :key="problem.field ?? ''">
              <template v-if="index > 0">; </template>
              <code v-if="problem.field">{{ problem.field }}</code>
              {{ problem.messages.join(' ') }}
            </template>
          </li>
        </ul>
      </Alert>
      <ErrorState v-else-if="send.error.value" :error="send.error.value" />
      <div>
        <Button type="submit" variant="primary" :loading="send.isPending.value"
          >Upload new version</Button
        >
      </div>
    </Stack>
  </form>
</template>
