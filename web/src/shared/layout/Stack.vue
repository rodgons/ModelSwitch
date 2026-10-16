<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { type Gap, gaps } from './gap.styles'
import { primitives } from './primitives.styles'

const props = withDefaults(defineProps<{ gap?: Gap; as?: string }>(), { gap: 12, as: 'div' })
const attrs = computed(() => stylex.props(primitives.stack, gaps[props.gap]))
</script>

<template>
  <component :is="as" v-bind="attrs"><slot /></component>
</template>
