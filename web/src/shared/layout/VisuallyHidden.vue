<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { primitives } from './primitives.styles'

const s = stylex.props(primitives.visuallyHidden)
</script>

<template>
  <span v-bind="s"><slot /></span>
</template>
