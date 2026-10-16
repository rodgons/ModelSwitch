<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { type Gap, gaps } from './gap.styles'
import { layout } from '@/shared/design/layout.stylex'
import { primitives } from './primitives.styles'

/** Columns follow the available width: repeat(auto-fit, minmax(min(<min>, 100%), 1fr)), <min> being a layout token. */
const props = withDefaults(
  defineProps<{ min?: 'narrow' | 'field' | 'card' | 'chart'; gap?: Gap }>(),
  {
    min: 'card',
    gap: 24,
  },
)
const minimums = {
  narrow: layout.columnNarrow,
  field: layout.columnField,
  card: layout.columnCard,
  chart: layout.columnChart,
}
const attrs = computed(() => stylex.props(primitives.grid(minimums[props.min]), gaps[props.gap]))
</script>

<template>
  <div v-bind="attrs"><slot /></div>
</template>
