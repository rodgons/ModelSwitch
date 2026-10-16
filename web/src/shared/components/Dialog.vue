<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import PrimeDialog from 'primevue/dialog'
import { computed } from 'vue'
import Inline from '@/shared/layout/Inline.vue'
import { dialog, typo } from './components.styles'

/**
 * A modal dialog: PrimeVue (unstyled) traps focus, restores it on close and handles Escape; the look comes from the design tokens
 * through pass-through classes (design-system.md section 8.1).
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = withDefaults(
  defineProps<{ title: string; wide?: boolean; dismissable?: boolean }>(),
  { wide: false, dismissable: true },
)
// The close button PrimeVue would focus is hidden (Cancel is in the actions), so focus the first field, or else the first button.
function focusFirst() {
  const dialogs = document.querySelectorAll<HTMLElement>('[role="dialog"]')
  const dialog = dialogs[dialogs.length - 1]
  const target =
    dialog?.querySelector<HTMLElement>('input, select, textarea') ??
    dialog?.querySelector<HTMLElement>('button')
  target?.focus()
}

const pt = computed(() => ({
  mask: { class: stylex.props(dialog.mask).className },
  root: { class: stylex.props(dialog.root, props.wide && dialog.wide).className },
  title: { class: stylex.props(typo.title).className },
  headerActions: { class: stylex.props(dialog.hidden).className },
}))
</script>

<template>
  <PrimeDialog
    v-model:visible="visible"
    :header="title"
    modal
    unstyled
    :closable="dismissable"
    :close-on-escape="dismissable"
    :dismissable-mask="dismissable"
    :pt="pt"
    :draggable="false"
    @show="focusFirst"
  >
    <slot />
    <template #footer>
      <Inline justify="end" :gap="8"><slot name="actions" /></Inline>
    </template>
  </PrimeDialog>
</template>
