<script setup lang="ts">
import { useMutation, useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { type RouteLocationRaw, useRouter } from 'vue-router'
import Button from '@/shared/components/Button.vue'
import ConfirmDialog from '@/shared/components/ConfirmDialog.vue'
import ErrorState from '@/shared/components/ErrorState.vue'

/**
 * Deletes one piece of evaluation content (evaluation.md section 8). The opening button is a destructive secondary; the dialog names
 * the object and what goes with it, and stays disabled until the Administrator types the object's name (design-system.md section
 * 8.1). A refusal (409 while it is still in use or running) is shown in the dialog. On success the matching queries are dropped and
 * the page leaves for `to`.
 */
const props = defineProps<{
  /** "Dataset", "Prompt" or "Comparison". */
  kind: string
  name: string
  remove: () => Promise<void>
  invalidate: readonly unknown[]
  to: RouteLocationRaw
}>()
const open = ref(false)
const router = useRouter()
const queries = useQueryClient()
const action = computed(() => `Delete ${props.kind.toLowerCase()}`)
const removal = useMutation({
  mutationFn: () => props.remove(),
  onSuccess: async () => {
    open.value = false
    queries.removeQueries({ queryKey: props.invalidate })
    await queries.invalidateQueries({ queryKey: props.invalidate })
    await router.push(props.to)
  },
})
const show = () => {
  removal.reset()
  open.value = true
}
</script>

<template>
  <Button destructive @click="show">{{ action }}</Button>
  <ConfirmDialog
    v-model:visible="open"
    :title="`Delete ${kind.toLowerCase()} ${name}?`"
    :action="action"
    :confirm-text="name"
    :pending="removal.isPending.value"
    @confirm="removal.mutate()"
  >
    <slot />
    <p>This cannot be undone.</p>
    <ErrorState v-if="removal.error.value" :error="removal.error.value" />
  </ConfirmDialog>
</template>
