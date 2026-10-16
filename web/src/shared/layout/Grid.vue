<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { type Gap, gaps } from './gap.styles'
import { primitives } from './primitives.styles'

/** Columns follow the available width: repeat(auto-fit, minmax(min(<min>, 100%), 1fr)). */
const props = withDefaults(defineProps<{ min?: string; gap?: Gap }>(), { min: '280px', gap: 24 })
const attrs = computed(() => stylex.props(primitives.grid(props.min), gaps[props.gap]))
</script>

<template>
  <div v-bind="attrs"><slot /></div>
</template>
