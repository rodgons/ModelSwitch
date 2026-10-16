<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { formatMoney } from '@/shared/format'
import VisuallyHidden from '@/shared/layout/VisuallyHidden.vue'
import EstimatedBadge from './EstimatedBadge.vue'
import { typo } from './components.styles'

/**
 * An Estimated Cost. `estimated` has no default on purpose: every place that shows money must decide, and the gateway's figures
 * always pass `true` (frontend.md section 3, "Estimated is never omitted").
 */
withDefaults(
  defineProps<{ amount: number | null | undefined; estimated: boolean; badge?: boolean }>(),
  { badge: true },
)
const numeric = stylex.props(typo.numeric)
</script>

<template>
  <span>
    <span v-bind="numeric">{{ formatMoney(amount) }}</span>
    <template v-if="estimated">
      <template v-if="badge">{{ ' ' }}<EstimatedBadge /></template>
      <VisuallyHidden v-else>{{ ' (estimated)' }}</VisuallyHidden>
    </template>
  </span>
</template>
