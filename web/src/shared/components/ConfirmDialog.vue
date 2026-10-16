<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Stack from '@/shared/layout/Stack.vue'
import Button from './Button.vue'
import Dialog from './Dialog.vue'
import Field from './Field.vue'
import TextInput from './TextInput.vue'

/**
 * Names the object and the consequence. For an irreversible action on a named object the confirm button stays disabled until the
 * Administrator types the object's identifier (design-system.md section 8.1).
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = withDefaults(
  defineProps<{ title: string; action: string; confirmText?: string; pending?: boolean }>(),
  { confirmText: undefined, pending: false },
)
const emit = defineEmits<{ confirm: [] }>()
const typed = ref('')
watch(visible, () => (typed.value = ''))
const ready = computed(() => !props.confirmText || typed.value === props.confirmText)
</script>

<template>
  <Dialog v-model:visible="visible" :title="title">
    <Stack :gap="16">
      <slot />
      <Field v-if="confirmText" v-slot="{ id }" :label="`Type ${confirmText} to confirm`">
        <TextInput :id="id" v-model="typed" mono />
      </Field>
    </Stack>
    <template #actions>
      <Button @click="visible = false">Cancel</Button>
      <Button variant="danger" :disabled="!ready" :loading="pending" @click="emit('confirm')">{{
        action
      }}</Button>
    </template>
  </Dialog>
</template>
