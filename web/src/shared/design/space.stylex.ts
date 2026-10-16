import * as stylex from '@stylexjs/stylex'

// design-system.md sections 4, 6 and 11: the 4 px grid, radii by role, motion.
export const space = stylex.defineVars({
  s0: '0',
  s2: '2px',
  s4: '4px',
  s6: '6px',
  s8: '8px',
  s12: '12px',
  s16: '16px',
  s20: '20px',
  s24: '24px',
  s32: '32px',
  s40: '40px',
  s48: '48px',
  s64: '64px',
})

export const radius = stylex.defineVars({
  hair: '2px',
  control: '6px',
  surface: '8px',
  dialog: '12px',
  full: '9999px',
})

export const motion = stylex.defineVars({
  fast: '120ms',
  base: '200ms',
  easing: 'cubic-bezier(0.2, 0, 0, 1)',
})
