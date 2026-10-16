<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { RouterLink, type RouteLocationRaw } from 'vue-router'
import { button } from './components.styles'

/** design-system.md section 7. Labels are verbs in sentence case; at most one primary per header, dialog or form. */
const props = withDefaults(
  defineProps<{
    variant?: 'primary' | 'secondary' | 'ghost' | 'danger'
    size?: 'sm' | 'md' | 'lg'
    type?: 'button' | 'submit'
    to?: RouteLocationRaw
    disabled?: boolean
    loading?: boolean
    destructive?: boolean
  }>(),
  {
    variant: 'secondary',
    size: 'md',
    type: 'button',
    to: undefined,
    disabled: false,
    loading: false,
    destructive: false,
  },
)
const attrs = computed(() =>
  stylex.props(
    button.base,
    button[props.size],
    button[props.variant],
    props.destructive && button.dangerText,
    (props.disabled || props.loading) && button.disabled,
  ),
)
</script>

<template>
  <RouterLink v-if="to" :to="to" v-bind="attrs"><slot /></RouterLink>
  <button
    v-else
    :type="type"
    :disabled="disabled || loading"
    :aria-busy="loading || undefined"
    v-bind="attrs"
  >
    <slot />
  </button>
</template>
