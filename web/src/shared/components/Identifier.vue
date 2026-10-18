<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, ref } from 'vue'
import { truncateMiddle } from '@/shared/format'
import Inline from '@/shared/layout/Inline.vue'
import Button from './Button.vue'
import { typo } from './components.styles'

/** A machine string truncated in the middle, the full value in the tooltip, with an optional copy button (design-system.md section 3.2). */
const props = withDefaults(
  defineProps<{ value: string; head?: number; tail?: number; copy?: boolean }>(),
  { head: 8, tail: 4, copy: false },
)
const copied = ref(false)
const shown = computed(() => truncateMiddle(props.value, props.head, props.tail))
const mono = computed(() =>
  stylex.props(typo.mono, shown.value === props.value ? typo.identifierWhole : typo.identifier),
)

async function copyValue() {
  await navigator.clipboard?.writeText(props.value)
  copied.value = true
  setTimeout(() => (copied.value = false), 1500)
}
</script>

<template>
  <Inline :gap="6" :wrap="false" as="span">
    <span v-bind="mono" :title="value">{{ shown }}</span>
    <Button
      v-if="copy"
      variant="ghost"
      size="sm"
      :aria-label="`Copy ${value}`"
      @click="copyValue"
      >{{ copied ? 'Copied' : 'Copy' }}</Button
    >
  </Inline>
</template>
