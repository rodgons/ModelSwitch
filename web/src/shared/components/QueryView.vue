<script setup lang="ts" generic="T">
import EmptyState from './EmptyState.vue'
import ErrorState from './ErrorState.vue'
import LoadingState from './LoadingState.vue'

/**
 * The four states every query-driven view handles (frontend.md section 3): loading (skeleton), error (message, code, request id,
 * retry), empty (explanation and next action), and data.
 */
const props = withDefaults(
  defineProps<{
    data: T | undefined
    isPending: boolean
    error: unknown
    refetch?: () => unknown
    isEmpty?: (data: T) => boolean
    emptyText?: string
  }>(),
  { refetch: undefined, isEmpty: () => false, emptyText: 'Nothing here yet.' },
)
const retry = () => void props.refetch?.()
</script>

<template>
  <LoadingState v-if="isPending" />
  <ErrorState v-else-if="error" :error="error" :retry="refetch ? retry : undefined" />
  <EmptyState v-else-if="data === undefined || isEmpty(data)" :text="emptyText"
    ><slot name="empty"
  /></EmptyState>
  <slot v-else :data="data as T" />
</template>
