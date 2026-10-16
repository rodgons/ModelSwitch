<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, watch } from 'vue'
import Alert from '@/shared/components/Alert.vue'
import Button from '@/shared/components/Button.vue'
import Checkbox from '@/shared/components/Checkbox.vue'
import { table, typo } from '@/shared/components/components.styles'
import Field from '@/shared/components/Field.vue'
import SegmentedControl from '@/shared/components/SegmentedControl.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import TextInput from '@/shared/components/TextInput.vue'
import Grid from '@/shared/layout/Grid.vue'
import Inline from '@/shared/layout/Inline.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { usePromptVersions } from '../queries'
import {
  collectErrors,
  comparators,
  type ConfigForm,
  evaluatorKindsFor,
  formConfigJson,
} from '../runForm'

/** One Run Configuration: target, Prompt Version, generation parameters and Evaluator (evaluation.md section 4). */
const config = defineModel<ConfigForm>('config', { required: true })
const props = defineProps<{
  index: number
  taskKind: string
  prompts: { value: string; label: string }[]
  aliases: { value: string; label: string }[]
  models: { value: string; label: string }[]
  errors: Record<string, string[]>
  canRemove: boolean
  canDuplicate: boolean
}>()
const emit = defineEmits<{ remove: []; duplicate: [] }>()

const versions = usePromptVersions(() => config.value.promptId)
const versionOptions = computed(() => [
  { value: '', label: 'Choose a version' },
  ...(versions.data.value?.items ?? []).map((v) => ({
    value: String(v.version),
    label: `v${v.version}`,
  })),
])
// A newly chosen Prompt starts on its latest version.
watch(
  () => versions.data.value,
  (page) => {
    const has = page?.items.some((v) => String(v.version) === config.value.promptVersion)
    if (page && !has)
      config.value.promptVersion = page.items[0] ? String(page.items[0].version) : ''
  },
)

const targetKinds: { value: ConfigForm['targetKind']; label: string }[] = [
  { value: 'alias', label: 'Model Alias' },
  { value: 'providerModel', label: 'Provider Model' },
]
const configModes: { value: ConfigForm['configMode']; label: string }[] = [
  { value: 'form', label: 'Form' },
  { value: 'json', label: 'JSON' },
]
const configMode = computed({
  get: () => config.value.configMode,
  set: (mode) => {
    if (mode === 'json' && config.value.configMode === 'form')
      config.value.configJson = formConfigJson(config.value)
    config.value.configMode = mode
  },
})
const kindOptions = computed(() =>
  evaluatorKindsFor(props.taskKind).map((k) => ({ value: k, label: k })),
)
const comparatorOptions = [
  { value: '', label: 'Default' },
  ...comparators.map((c) => ({ value: c, label: c })),
]
const defaultComparatorOptions = comparators.map((c) => ({ value: c, label: c }))
const withPlaceholder = (options: { value: string; label: string }[], label: string) => [
  { value: '', label },
  ...options,
]

const e = (key: string) => props.errors[key]
const all = (prefix: string) => collectErrors(props.errors, prefix)
// Config errors the kind's form shows next to a field; the rest are listed together below the form.
const shownByKind: Record<string, (sub: string) => boolean> = {
  classification: () => true,
  json_fields: (sub) =>
    sub === 'defaultComparator' || sub === 'passThreshold' || sub.startsWith('fields'),
  llm_judge: (sub) => ['judgeAlias', 'rubric', 'passThreshold'].includes(sub),
  reference_similarity: (sub) => sub === 'passThreshold',
}
const unplaced = computed(() => {
  if (config.value.configMode === 'json') return []
  const shown = shownByKind[config.value.evaluatorKind] ?? (() => false)
  return Object.entries(props.errors).flatMap(([key, messages]) => {
    if (key === 'evaluator.config') return shown('') ? [] : messages
    if (!key.startsWith('evaluator.config.')) return []
    const sub = key.slice('evaluator.config.'.length)
    return shown(sub) ? [] : messages.map((m) => `${sub}: ${m}`)
  })
})
const general = computed(() => [...(props.errors[''] ?? []), ...all('evaluator.version')])
const s = {
  caption: stylex.props(typo.caption),
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  td: stylex.props(table.td),
  mono: stylex.props(typo.mono),
}
</script>

<template>
  <Section :title="`Configuration ${index + 1}`" surface>
    <template #actions>
      <Inline :gap="4">
        <Button v-if="canDuplicate" variant="ghost" size="sm" @click="emit('duplicate')"
          >Duplicate</Button
        >
        <Button v-if="canRemove" variant="ghost" size="sm" destructive @click="emit('remove')"
          >Remove</Button
        >
      </Inline>
    </template>
    <Stack :gap="16">
      <Alert v-if="general.length" tone="danger">{{ general.join(' ') }}</Alert>
      <Stack :gap="8">
        <SegmentedControl
          v-model="config.targetKind"
          :label="`Configuration ${index + 1} target kind`"
          :options="targetKinds"
        />
        <Field
          v-if="config.targetKind === 'alias'"
          v-slot="{ id }"
          label="Model Alias"
          help="Routed by the alias's Policy Version current when the run is created."
          :errors="all('target')"
          ><SelectInput
            :id="id"
            v-model="config.alias"
            :options="withPlaceholder(aliases, 'Choose a Model Alias')"
        /></Field>
        <Field
          v-else
          v-slot="{ id }"
          label="Provider Model"
          help="Pinned: bypasses routing, still within the Tenant's Budget and model access."
          :errors="all('target')"
          ><SelectInput
            :id="id"
            v-model="config.providerModelId"
            :options="withPlaceholder(models, 'Choose a Provider Model')"
        /></Field>
      </Stack>
      <Grid min="field" :gap="12">
        <Field
          v-slot="{ id }"
          label="Prompt"
          :errors="[...(e('prompt') ?? []), ...(e('prompt.id') ?? [])]"
          ><SelectInput
            :id="id"
            v-model="config.promptId"
            :options="withPlaceholder(prompts, 'Choose a Prompt')"
        /></Field>
        <Field v-slot="{ id }" label="Prompt Version" :errors="e('prompt.version')"
          ><SelectInput :id="id" v-model="config.promptVersion" :options="versionOptions"
        /></Field>
      </Grid>
      <Grid min="narrow" :gap="12">
        <Field
          v-slot="{ id, invalid }"
          label="Temperature"
          help="0–2. Empty: model default."
          :errors="e('params.temperature')"
          ><TextInput :id="id" v-model="config.temperature" inputmode="decimal" :invalid="invalid"
        /></Field>
        <Field
          v-slot="{ id, invalid }"
          label="Top P"
          help="0–1. Empty: default."
          :errors="e('params.topP')"
          ><TextInput :id="id" v-model="config.topP" inputmode="decimal" :invalid="invalid"
        /></Field>
        <Field v-slot="{ id, invalid }" label="Seed" help="Whole number." :errors="e('params.seed')"
          ><TextInput :id="id" v-model="config.seed" inputmode="numeric" :invalid="invalid"
        /></Field>
        <Field
          v-slot="{ id, invalid }"
          label="Max output tokens"
          help="Bounds the worst case."
          :errors="e('params.maxOutputTokens')"
          ><TextInput
            :id="id"
            v-model="config.maxOutputTokens"
            inputmode="numeric"
            :invalid="invalid"
        /></Field>
      </Grid>
      <Stack :gap="12">
        <Inline justify="between" align="end" :gap="12">
          <Field v-slot="{ id }" label="Evaluator" :errors="all('evaluator.kind')"
            ><SelectInput :id="id" v-model="config.evaluatorKind" :options="kindOptions"
          /></Field>
          <SegmentedControl
            v-model="configMode"
            :label="`Configuration ${index + 1} Evaluator config view`"
            :options="configModes"
          />
        </Inline>
        <Alert v-if="config.evaluatorKind === 'llm_judge'" tone="info"
          >Model-judged: a judge model scores each output. Its calls are Inference Requests of their
          own and count toward the Budget and the run's cost cap.</Alert
        >
        <Field
          v-if="configMode === 'json'"
          v-slot="{ id, invalid }"
          label="Evaluator config (JSON)"
          help="Sent as the Evaluator's config. Empty: none."
          :errors="all('evaluator.config')"
          ><TextInput :id="id" v-model="config.configJson" multiline mono :invalid="invalid"
        /></Field>
        <template v-else>
          <Checkbox
            v-if="config.evaluatorKind === 'exact_match'"
            v-model="config.caseFold"
            label="Ignore case"
          />
          <Field
            v-if="config.evaluatorKind === 'classification'"
            v-slot="{ id, invalid }"
            label="Labels"
            help="One per line, from the Dataset Version's expected labels. Outputs that match none are unparseable."
            :errors="all('evaluator.config')"
            ><TextInput :id="id" v-model="config.labels" multiline mono :invalid="invalid"
          /></Field>
          <template v-if="config.evaluatorKind === 'json_fields'">
            <Field
              v-slot="{ id }"
              label="Default comparator"
              :errors="e('evaluator.config.defaultComparator')"
              ><SelectInput
                :id="id"
                v-model="config.defaultComparator"
                :options="defaultComparatorOptions"
            /></Field>
            <TableScroll v-if="config.fields.length" label="Field comparators">
              <table v-bind="s.table">
                <thead>
                  <tr>
                    <th v-bind="s.th" scope="col">Field</th>
                    <th v-bind="s.th" scope="col">Comparator</th>
                    <th v-bind="s.th" scope="col">Tolerance</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="field in config.fields" :key="field.name">
                    <th v-bind="s.td" scope="row">
                      <span v-bind="s.mono">{{ field.name }}</span>
                    </th>
                    <td v-bind="s.td">
                      <SelectInput
                        v-model="field.comparator"
                        :options="comparatorOptions"
                        :aria-label="`Comparator for ${field.name}`"
                      />
                    </td>
                    <td v-bind="s.td">
                      <TextInput
                        v-if="field.comparator === 'number'"
                        v-model="field.tolerance"
                        inputmode="decimal"
                        :aria-label="`Tolerance for ${field.name}`"
                      />
                    </td>
                  </tr>
                </tbody>
              </table>
            </TableScroll>
            <span v-else v-bind="s.caption"
              >Choose a Dataset Version to list its expected fields.</span
            >
            <Alert v-if="all('evaluator.config.fields').length" tone="danger">{{
              all('evaluator.config.fields').join(' ')
            }}</Alert>
          </template>
          <template v-if="config.evaluatorKind === 'llm_judge'">
            <Field
              v-slot="{ id }"
              label="Judge Model Alias"
              :errors="e('evaluator.config.judgeAlias')"
              ><SelectInput
                :id="id"
                v-model="config.judgeAlias"
                :options="withPlaceholder(aliases, 'Choose a judge alias')"
            /></Field>
            <Field v-slot="{ id, invalid }" label="Rubric" :errors="e('evaluator.config.rubric')"
              ><TextInput :id="id" v-model="config.rubric" multiline :invalid="invalid"
            /></Field>
          </template>
          <Field
            v-if="
              ['json_fields', 'llm_judge', 'reference_similarity'].includes(config.evaluatorKind)
            "
            v-slot="{ id, invalid }"
            label="Pass threshold"
            help="0–1: the score a Case needs to pass. Empty: the Evaluator's default."
            :errors="e('evaluator.config.passThreshold')"
            ><TextInput
              :id="id"
              v-model="config.passThreshold"
              inputmode="decimal"
              :invalid="invalid"
          /></Field>
          <Alert v-if="unplaced.length" tone="danger">{{ unplaced.join(' ') }}</Alert>
        </template>
      </Stack>
    </Stack>
  </Section>
</template>
