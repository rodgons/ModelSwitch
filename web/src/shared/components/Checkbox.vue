<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { field } from './components.styles'

const model = defineModel<boolean>({ default: false })
defineProps<{ label: string }>()
const attrs = stylex.props(field.checkbox)
</script>

<template>
  <label v-bind="attrs"><input v-model="model" type="checkbox" />{{ label }}</label>
</template>
