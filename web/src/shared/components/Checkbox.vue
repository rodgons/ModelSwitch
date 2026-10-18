<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { field } from './components.styles'

const model = defineModel<boolean>({ default: false })
defineProps<{ label: string }>()
const attrs = stylex.props(field.checkbox)
const input = stylex.props(field.checkboxInput)
</script>

<template>
  <label v-bind="attrs"><input v-model="model" type="checkbox" v-bind="input" />{{ label }}</label>
</template>
