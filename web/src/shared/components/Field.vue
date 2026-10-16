<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { useId } from 'vue'
import { field } from './components.styles'

/** A label above its control, then help text or the server's validation message (design-system.md section 8.1). */
defineProps<{ label: string; help?: string; errors?: string[] }>()
defineSlots<{ default(props: { id: string; invalid: boolean; describedBy: string }): unknown }>()
const id = useId()
const s = {
  field: stylex.props(field.field),
  label: stylex.props(field.label),
  help: stylex.props(field.help),
  error: stylex.props(field.error),
}
</script>

<template>
  <div v-bind="s.field">
    <label :for="id" v-bind="s.label">{{ label }}</label>
    <slot :id="id" :invalid="(errors?.length ?? 0) > 0" :described-by="`${id}-help`" />
    <p v-if="errors?.length" :id="`${id}-help`" v-bind="s.error" role="alert">
      {{ errors.join(' ') }}
    </p>
    <p v-else-if="help" :id="`${id}-help`" v-bind="s.help">{{ help }}</p>
  </div>
</template>
