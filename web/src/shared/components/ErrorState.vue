<script setup lang="ts">
import { computed } from 'vue'
import { ApiError } from '@/api/client'
import Facts from '@/shared/layout/Facts.vue'
import Alert from './Alert.vue'
import Button from './Button.vue'

/** What happened, the error code and the request id, and a way to try again (design-system.md section 12). */
const props = defineProps<{ error: unknown; retry?: () => void }>()
const api = computed(() => (props.error instanceof ApiError ? props.error : null))
const message = computed(() =>
  props.error instanceof Error ? props.error.message : 'The dashboard could not load this.',
)
const facts = computed(() => {
  const items = [{ label: 'Error code', value: api.value?.errorCode ?? 'network_error' }]
  if (api.value?.requestId) items.push({ label: 'Request id', value: api.value.requestId })
  return items
})
</script>

<template>
  <Alert tone="danger">
    <span>{{ message }}</span>
    <Facts :items="facts" />
    <div v-if="retry"><Button size="sm" @click="retry">Try again</Button></div>
  </Alert>
</template>
