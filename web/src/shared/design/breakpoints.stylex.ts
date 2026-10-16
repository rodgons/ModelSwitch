import * as stylex from '@stylexjs/stylex'

// design-system.md section 9.1: mobile-first min-width queries, declared once.
export const bp = stylex.defineConsts({
  sm: '@media (min-width: 640px)',
  md: '@media (min-width: 768px)',
  lg: '@media (min-width: 1024px)',
  xl: '@media (min-width: 1280px)',
  xxl: '@media (min-width: 1536px)',
  reducedMotion: '@media (prefers-reduced-motion: reduce)',
})
