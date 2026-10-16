<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { primitives } from './primitives.styles'

defineProps<{ title: string; lede?: string }>()
const s = {
  header: stylex.props(primitives.pageHeader),
  heading: stylex.props(primitives.heading),
  lede: stylex.props(primitives.lede),
}
</script>

<template>
  <header v-bind="s.header">
    <div>
      <h1 v-bind="s.heading">
        <slot name="title">{{ title }}</slot>
      </h1>
      <p v-if="lede || $slots.lede" v-bind="s.lede">
        <slot name="lede">{{ lede }}</slot>
      </p>
    </div>
    <div v-if="$slots.actions"><slot name="actions" /></div>
  </header>
</template>
