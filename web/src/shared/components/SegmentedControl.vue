<script setup lang="ts" generic="T extends string">
import * as stylex from '@stylexjs/stylex'
import { segmented } from './components.styles'

/** 2–4 mutually exclusive views; each segment is a button with aria-pressed (design-system.md section 8.1). */
const model = defineModel<T>({ required: true })
defineProps<{ label: string; options: { value: T; label: string }[] }>()
const track = stylex.props(segmented.track)
const segment = (selected: boolean) =>
  stylex.props(segmented.segment, selected && segmented.selected)
</script>

<template>
  <div v-bind="track" role="group" :aria-label="label">
    <button
      v-for="option in options"
      :key="option.value"
      type="button"
      :aria-pressed="model === option.value"
      v-bind="segment(model === option.value)"
      @click="model = option.value"
    >
      {{ option.label }}
    </button>
  </div>
</template>
