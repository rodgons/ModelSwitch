import * as stylex from '@stylexjs/stylex'
import { ref, watchEffect } from 'vue'
import { darkTheme, lightTheme } from '@/shared/design/themes'

export type ThemeChoice = 'system' | 'light' | 'dark'
const storageKey = 'modelswitch.theme'

function stored(): ThemeChoice {
  try {
    const value = localStorage.getItem(storageKey)
    return value === 'light' || value === 'dark' ? value : 'system'
  } catch {
    return 'system'
  }
}

/** The Administrator's theme override, kept in localStorage and applied on <html> (design-system.md section 1). */
export const theme = ref<ThemeChoice>(stored())

export function applyTheme(root: HTMLElement = document.documentElement) {
  const classes = {
    light: stylex.props(lightTheme).className ?? '',
    dark: stylex.props(darkTheme).className ?? '',
  }
  watchEffect(() => {
    root.classList.remove(
      ...[classes.light, classes.dark].flatMap((c) => c.split(' ')).filter(Boolean),
    )
    if (theme.value !== 'system')
      root.classList.add(...classes[theme.value].split(' ').filter(Boolean))
    try {
      localStorage.setItem(storageKey, theme.value)
    } catch {
      // Private mode: the choice lasts for this page only.
    }
  })
}
