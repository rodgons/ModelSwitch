<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { tone as tones } from './components.styles'

/** 24 px trend line for a Readout figure. Decorative: the figure itself carries the meaning (design-system.md section 8.1). */
const props = withDefaults(
  defineProps<{ values: (number | null)[]; tone?: 'accent' | 'warning' | 'danger' | 'success' }>(),
  { tone: 'accent' },
)
const width = 120
const height = 24
const points = computed(() => {
  const known = props.values.map((v) => v ?? 0)
  if (known.length < 2) return []
  const max = Math.max(...known)
  const min = Math.min(...known)
  const span = max - min || 1
  return known.map(
    (v, i) =>
      [(i / (known.length - 1)) * width, height - 2 - ((v - min) / span) * (height - 4)] as const,
  )
})
const line = computed(() =>
  points.value.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(' '),
)
const area = computed(() =>
  points.value.length ? `0,${height} ${line.value} ${width},${height}` : '',
)
const last = computed(() => points.value.at(-1))
const toneAttrs = computed(() => stylex.props(tones[props.tone]))
</script>

<template>
  <svg
    v-if="points.length"
    :viewBox="`0 0 ${width} ${height}`"
    preserveAspectRatio="none"
    width="100%"
    :height="height"
    aria-hidden="true"
    v-bind="toneAttrs"
  >
    <polygon :points="area" fill="currentColor" fill-opacity="0.12" />
    <polyline
      :points="line"
      fill="none"
      stroke="currentColor"
      stroke-width="1.5"
      vector-effect="non-scaling-stroke"
    />
    <circle v-if="last" :cx="last[0]" :cy="last[1]" r="2.5" fill="currentColor" />
  </svg>
</template>
