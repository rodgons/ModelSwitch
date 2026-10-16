<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { type Gap, gaps } from './gap.styles'
import { primitives } from './primitives.styles'

const props = withDefaults(
  defineProps<{
    gap?: Gap
    align?: 'start' | 'center' | 'baseline' | 'end'
    justify?: 'start' | 'between' | 'end'
    wrap?: boolean
    as?: string
  }>(),
  { gap: 8, align: 'center', justify: 'start', wrap: true, as: 'div' },
)
const alignments = {
  start: primitives.alignStart,
  center: primitives.alignCenter,
  baseline: primitives.alignBaseline,
  end: primitives.alignEnd,
}
const justifications = {
  start: primitives.justifyStart,
  between: primitives.justifyBetween,
  end: primitives.justifyEnd,
}
const attrs = computed(() =>
  stylex.props(
    primitives.inline,
    gaps[props.gap],
    alignments[props.align],
    justifications[props.justify],
    !props.wrap && primitives.noWrap,
  ),
)
</script>

<template>
  <component :is="as" v-bind="attrs"><slot /></component>
</template>
