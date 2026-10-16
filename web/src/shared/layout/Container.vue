<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { primitives } from './primitives.styles'

const props = withDefaults(defineProps<{ size?: 'page' | 'wide' | 'form' }>(), { size: 'page' })
const attrs = computed(() => stylex.props(primitives.container, primitives[props.size]))
</script>

<template>
  <div v-bind="attrs"><slot /></div>
</template>
