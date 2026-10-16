<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { state, typo } from './components.styles'

/** One sentence and, when there is one, the next action (design-system.md section 12). */
defineProps<{ text: string }>()
const s = { root: stylex.props(state.empty), text: stylex.props(typo.lede) }
</script>

<template>
  <div v-bind="s.root">
    <p v-bind="s.text">{{ text }}</p>
    <slot />
  </div>
</template>
