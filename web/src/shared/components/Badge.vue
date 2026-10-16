<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { badge } from './components.styles'

/** A status is never color alone: the label names the state and the dot repeats it (design-system.md section 8.1). */
const props = withDefaults(
  defineProps<{
    tone?: 'success' | 'warning' | 'danger' | 'info' | 'neutral' | 'accent'
    dot?: boolean
  }>(),
  {
    tone: 'neutral',
    dot: true,
  },
)
const attrs = computed(() => stylex.props(badge.base, badge[props.tone]))
const dotAttrs = stylex.props(badge.dot)
</script>

<template>
  <span v-bind="attrs"><span v-if="dot" v-bind="dotAttrs" aria-hidden="true" /><slot /></span>
</template>
