<script setup lang="ts">
import Inline from './Inline.vue'
</script>

<template>
  <Inline :gap="8" align="end" role="toolbar"><slot /></Inline>
</template>
