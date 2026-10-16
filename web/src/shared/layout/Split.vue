<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { primitives } from './primitives.styles'

/** Main column plus a 360 px aside from the breakpoint; stacked below it. */
const props = withDefaults(defineProps<{ asideAt?: 'lg' | 'xl' }>(), { asideAt: 'xl' })
const attrs = computed(() =>
  stylex.props(
    primitives.split,
    props.asideAt === 'lg' ? primitives.splitAtLg : primitives.splitAtXl,
  ),
)
</script>

<template>
  <div v-bind="attrs">
    <div><slot /></div>
    <aside><slot name="aside" /></aside>
  </div>
</template>
