<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { primitives } from './primitives.styles'

/** Wide tables scroll inside themselves, never the page. Focusable so keyboard users can scroll it. */
defineProps<{ label: string }>()
const s = stylex.props(primitives.tableScroll)
</script>

<template>
  <div v-bind="s" tabindex="0" role="region" :aria-label="label"><slot /></div>
</template>
