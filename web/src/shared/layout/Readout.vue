<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import EstimatedBadge from '@/shared/components/EstimatedBadge.vue'
import Sparkline from '@/shared/components/Sparkline.vue'
import { primitives } from './primitives.styles'

/** One band of figures separated by hairlines; replaces stat cards (design-system.md section 9.2). */
export interface ReadoutItem {
  label: string
  value: string
  delta?: string
  trend?: (number | null)[]
  tone?: 'accent' | 'warning' | 'danger' | 'success'
  estimated?: boolean
}

defineProps<{ items: ReadoutItem[] }>()
const s = {
  band: stylex.props(primitives.readout),
  item: stylex.props(primitives.readoutItem),
  label: stylex.props(primitives.readoutLabel),
  value: stylex.props(primitives.readoutValue, primitives.reset),
  delta: stylex.props(primitives.readoutDelta, primitives.reset),
  trend: stylex.props(primitives.reset),
}
</script>

<template>
  <dl v-bind="s.band">
    <div v-for="item in items" :key="item.label" v-bind="s.item">
      <dt v-bind="s.label">{{ item.label }}<EstimatedBadge v-if="item.estimated" /></dt>
      <dd v-bind="s.value">{{ item.value }}</dd>
      <dd v-if="item.delta" v-bind="s.delta">{{ item.delta }}</dd>
      <dd v-if="item.trend" v-bind="s.trend">
        <Sparkline :values="item.trend" :tone="item.tone" />
      </dd>
    </div>
  </dl>
</template>
