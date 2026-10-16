<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import Stack from '@/shared/layout/Stack.vue'
import VisuallyHidden from '@/shared/layout/VisuallyHidden.vue'
import { state } from './components.styles'

withDefaults(defineProps<{ lines?: number }>(), { lines: 4 })
const line = stylex.props(state.skeleton)
</script>

<template>
  <Stack :gap="8" aria-busy="true">
    <VisuallyHidden role="status">Loading</VisuallyHidden>
    <div v-for="index in lines" :key="index" v-bind="line" aria-hidden="true" />
  </Stack>
</template>
