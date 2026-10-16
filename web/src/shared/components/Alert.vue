<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { alert } from './components.styles'

const props = withDefaults(defineProps<{ tone?: 'danger' | 'warning' | 'info' | 'success' }>(), {
  tone: 'info',
})
const attrs = computed(() => stylex.props(alert.base, alert[props.tone]))
</script>

<template>
  <div v-bind="attrs" :role="tone === 'danger' ? 'alert' : 'status'"><slot /></div>
</template>
