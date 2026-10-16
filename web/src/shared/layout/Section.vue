<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, useId } from 'vue'
import Inline from './Inline.vue'
import { primitives } from './primitives.styles'

/** A titled block on the canvas; `surface` gives it an edge for tables, forms and charts (design-system.md section 5). */
const props = withDefaults(defineProps<{ title: string; surface?: boolean; flush?: boolean }>(), {
  surface: false,
  flush: false,
})
const id = useId()
const s = {
  section: stylex.props(primitives.section),
  title: stylex.props(primitives.sectionTitle),
}
const body = computed(() =>
  props.surface ? stylex.props(primitives.surface, props.flush && primitives.flush) : {},
)
</script>

<template>
  <section v-bind="s.section" :aria-labelledby="id">
    <Inline justify="between" align="center">
      <h2 :id="id" v-bind="s.title">{{ title }}</h2>
      <slot name="actions" />
    </Inline>
    <div v-bind="body"><slot /></div>
  </section>
</template>
