<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import { field, typo } from './components.styles'

const model = defineModel<string>({ default: '' })
const props = withDefaults(
  defineProps<{
    id?: string
    type?: string
    mono?: boolean
    invalid?: boolean
    multiline?: boolean
    placeholder?: string
  }>(),
  {
    id: undefined,
    type: 'text',
    mono: false,
    invalid: false,
    multiline: false,
    placeholder: undefined,
  },
)
const attrs = computed(() =>
  stylex.props(
    field.control,
    props.multiline && field.textarea,
    props.mono && typo.mono,
    props.invalid && field.invalid,
  ),
)
</script>

<template>
  <textarea
    v-if="multiline"
    :id="id"
    v-model="model"
    v-bind="attrs"
    :aria-invalid="invalid || undefined"
    :placeholder="placeholder"
    spellcheck="false"
  />
  <input
    v-else
    :id="id"
    v-model="model"
    :type="type"
    v-bind="attrs"
    :aria-invalid="invalid || undefined"
    :placeholder="placeholder"
  />
</template>
