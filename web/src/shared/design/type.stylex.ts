import * as stylex from '@stylexjs/stylex'

// design-system.md section 3: one family in two widths, a 12 / 14 / 16 / 18 / 24 scale, three weights.
export const font = stylex.defineVars({
  sans: '"Atkinson Hyperlegible Next", system-ui, sans-serif',
  mono: '"Atkinson Hyperlegible Mono", ui-monospace, Menlo, monospace',
})

export const text = stylex.defineVars({
  captionSize: '12px',
  captionLine: '16px',
  bodySize: '14px',
  bodyLine: '20px',
  ledeSize: '16px',
  ledeLine: '24px',
  titleSize: '18px',
  titleLine: '24px',
  headingSize: '24px',
  headingLine: '32px',
  weightRegular: '400',
  weightMedium: '500',
  weightBold: '600',
  trackingCaption: '0.01em',
  trackingTitle: '-0.01em',
  trackingHeading: '-0.015em',
})
