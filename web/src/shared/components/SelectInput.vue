<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { field } from './components.styles'

const model = defineModel<string>({ default: '' })
defineProps<{ id?: string; options: { value: string; label: string }[] }>()
const attrs = stylex.props(field.control)
</script>

<template>
  <select :id="id" v-model="model" v-bind="attrs">
    <option v-for="option in options" :key="option.value" :value="option.value">
      {{ option.label }}
    </option>
  </select>
</template>
