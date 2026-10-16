<script setup lang="ts">
import { computed } from 'vue'
import Badge from './Badge.vue'

/** Request, Attempt, circuit, Evaluation Run and Case Result states, each with its status hue and a text label. */
const props = defineProps<{ status: string }>()
const tones: Record<string, 'success' | 'warning' | 'danger' | 'info' | 'neutral'> = {
  succeeded: 'success',
  closed: 'success',
  active: 'success',
  failed: 'danger',
  abandoned: 'danger',
  open: 'danger',
  ambiguous: 'warning',
  half_open: 'warning',
  cancelled: 'neutral',
  skipped_circuit_open: 'neutral',
  running: 'info',
  suspended: 'warning',
  unknown: 'neutral',
  queued: 'neutral',
  passed: 'success',
  errored: 'danger',
  skipped: 'neutral',
}
const labels: Record<string, string> = {
  half_open: 'Half-open',
  skipped_circuit_open: 'Skipped, circuit open',
}
const tone = computed(() => tones[props.status] ?? 'neutral')
const label = computed(
  () => labels[props.status] ?? props.status.charAt(0).toUpperCase() + props.status.slice(1),
)
</script>

<template>
  <Badge :tone="tone">{{ label }}</Badge>
</template>
