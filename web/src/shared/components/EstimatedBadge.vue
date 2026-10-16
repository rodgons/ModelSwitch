<script setup lang="ts">
import Badge from './Badge.vue'
</script>

<template>
  <Badge
    tone="info"
    :dot="false"
    title="Calculated by the gateway from token counts and the Price in force; not the Provider's charge"
    >Estimated</Badge
  >
</template>
