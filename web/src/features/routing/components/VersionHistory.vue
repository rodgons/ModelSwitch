<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, ref, watch } from 'vue'
import Button from '@/shared/components/Button.vue'
import QueryView from '@/shared/components/QueryView.vue'
import { formatTimestamp } from '@/shared/format'
import Inline from '@/shared/layout/Inline.vue'
import Stack from '@/shared/layout/Stack.vue'
import { diffLines, pretty } from '../diff'
import { useVersions } from '../queries'
import { diff } from '../routing.styles'

/** Immutable Policy Versions, newest first, and what changed in each against the one before. */
const props = defineProps<{ policyId: string }>()
const versions = useVersions(() => props.policyId)
const shown = ref<number | null>(null)
watch(
  () => props.policyId,
  () => (shown.value = null),
)

const lines = computed(() => {
  const items = versions.data.value?.items ?? []
  const index = items.findIndex((v) => v.version === shown.value)
  if (index < 0) return []
  return diffLines(pretty(items[index + 1]?.definition ?? {}), pretty(items[index]!.definition))
})
const block = stylex.props(diff.block)
const lineAttrs = (kind: 'same' | 'added' | 'removed') =>
  stylex.props(diff.line, kind === 'added' && diff.added, kind === 'removed' && diff.removed)
const marker = { same: ' ', added: '+', removed: '-' }
</script>

<template>
  <QueryView
    :data="versions.data.value"
    :is-pending="versions.isPending.value"
    :error="versions.error.value"
    :refetch="versions.refetch"
  >
    <template #default="{ data: page }">
      <Stack :gap="12">
        <Inline :gap="6">
          <Button
            v-for="version in page.items"
            :key="version.version"
            size="sm"
            :variant="shown === version.version ? 'primary' : 'secondary'"
            :title="formatTimestamp(version.createdAt)"
            @click="shown = version.version"
          >
            v{{ version.version }}
          </Button>
        </Inline>
        <pre
          v-if="shown !== null"
          v-bind="block"
          role="region"
          tabindex="0"
          :aria-label="`Changes in version ${shown}`"
        ><span v-for="(line, index) in lines" :key="index" v-bind="lineAttrs(line.kind)">{{ marker[line.kind] }} {{ line.text }}</span></pre>
      </Stack>
    </template>
  </QueryView>
</template>
