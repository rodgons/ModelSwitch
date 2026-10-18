<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { RouterLink, type RouteLocationRaw } from 'vue-router'
import Icon from '@/shared/components/Icon.vue'
import { link } from '@/shared/components/components.styles'
import { primitives } from './primitives.styles'

/** The page title, an optional lede and the page's actions. `back` names the list a detail page belongs to, above the title. */
const props = defineProps<{
  title: string
  lede?: string
  back?: { to: RouteLocationRaw; label: string }
}>()
const s = {
  header: stylex.props(primitives.pageHeader),
  heading: stylex.props(primitives.heading),
  lede: stylex.props(primitives.lede),
  back: stylex.props(link.quiet, link.focus, primitives.back),
  actions: stylex.props(props.back ? primitives.actionsBelowBack : null),
}
</script>

<template>
  <header v-bind="s.header">
    <div>
      <RouterLink v-if="back" :to="back.to" v-bind="s.back"
        ><Icon name="back" :size="14" />{{ back.label }}</RouterLink
      >
      <h1 v-bind="s.heading">
        <slot name="title">{{ title }}</slot>
      </h1>
      <p v-if="lede || $slots.lede" v-bind="s.lede">
        <slot name="lede">{{ lede }}</slot>
      </p>
    </div>
    <div v-if="$slots.actions" v-bind="s.actions"><slot name="actions" /></div>
  </header>
</template>
