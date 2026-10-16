<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { primitives } from './primitives.styles'

/** Label/value pairs as a <dl>; never a dot-joined string. A slot named after an item's `key` replaces its text. */
defineProps<{ items: { label: string; value: string; key?: string }[] }>()
const s = {
  list: stylex.props(primitives.facts),
  fact: stylex.props(primitives.fact),
  label: stylex.props(primitives.factLabel),
  value: stylex.props(primitives.factValue),
}
</script>

<template>
  <dl v-bind="s.list">
    <div v-for="item in items" :key="item.label" v-bind="s.fact">
      <dt v-bind="s.label">{{ item.label }}</dt>
      <dd v-bind="s.value">
        <slot :name="item.key ?? item.label" :item="item">{{ item.value }}</slot>
      </dd>
    </div>
  </dl>
</template>
