<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, data } from '@/api/client'
import { useTenants } from '@/features/tenants/queries'
import Button from '@/shared/components/Button.vue'
import Dialog from '@/shared/components/Dialog.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { useFormMutation } from '@/shared/forms'
import Stack from '@/shared/layout/Stack.vue'
import { taskKinds } from '../labels'
import { evaluationKeys } from '../queries'

/** Creates a Dataset (name, Tenant, task kind) or a Prompt (name, Tenant), then opens it. */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ kind: 'dataset' | 'prompt'; tenantId: string }>()
const router = useRouter()
const tenants = useTenants()
const tenantOptions = computed(() =>
  (tenants.data.value?.items ?? []).map((t) => ({ value: t.id, label: t.name })),
)
const form = reactive({ name: '', tenantId: '', taskKind: 'extraction' })
watch(visible, (open) => {
  if (!open) return
  Object.assign(form, {
    name: '',
    tenantId: props.tenantId || tenantOptions.value[0]?.value || '',
    taskKind: 'extraction',
  })
  save.reset()
})
watch(tenantOptions, (options) => {
  if (!form.tenantId && options.length) form.tenantId = options[0]!.value
})

const noun = computed(() => (props.kind === 'dataset' ? 'Dataset' : 'Prompt'))
const save = useFormMutation(
  async () => {
    if (props.kind === 'dataset') {
      const created = data(
        await api.POST('/admin/datasets', {
          body: { tenantId: form.tenantId || null, name: form.name, taskKind: form.taskKind },
        }),
      )
      return `/evaluations/datasets/${created.id}`
    }
    const created = data(
      await api.POST('/admin/prompts', {
        body: { tenantId: form.tenantId || null, name: form.name },
      }),
    )
    return `/evaluations/prompts/${created.id}`
  },
  [props.kind === 'dataset' ? evaluationKeys.datasets : evaluationKeys.prompts],
  (path) => {
    visible.value = false
    void router.push(path)
  },
)
</script>

<template>
  <Dialog v-model:visible="visible" :title="`Create ${noun}`">
    <form :id="`create-${kind}`" novalidate @submit.prevent="save.mutation.mutate(undefined)">
      <Stack :gap="12">
        <ErrorState v-if="save.generalError.value" :error="save.generalError.value" />
        <Field
          v-slot="{ id, invalid }"
          label="Name"
          help="Unique within the Tenant, up to 128 characters."
          :errors="save.errorsFor('name')"
          ><TextInput :id="id" v-model="form.name" :invalid="invalid"
        /></Field>
        <Field v-slot="{ id }" label="Tenant" :errors="save.errorsFor('tenantId')"
          ><SelectInput :id="id" v-model="form.tenantId" :options="tenantOptions"
        /></Field>
        <Field
          v-if="kind === 'dataset'"
          v-slot="{ id }"
          label="Task kind"
          help="Decides what each Case's expected value is and which Evaluators fit."
          :errors="save.errorsFor('taskKind')"
          ><SelectInput :id="id" v-model="form.taskKind" :options="taskKinds"
        /></Field>
      </Stack>
    </form>
    <template #actions>
      <Button @click="visible = false">Cancel</Button>
      <Button
        type="submit"
        :form="`create-${kind}`"
        variant="primary"
        :loading="save.mutation.isPending.value"
        >Create {{ noun }}</Button
      >
    </template>
  </Dialog>
</template>
