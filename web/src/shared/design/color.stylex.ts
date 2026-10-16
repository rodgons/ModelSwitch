import * as stylex from '@stylexjs/stylex'

// design-system.md section 2. Semantic aliases only: components never reference a raw step or a hex value.
// The default follows prefers-color-scheme; themes.ts pins light or dark when the Administrator chooses one.
export const color = stylex.defineVars({
  bgCanvas: { default: '#f8f7fa', '@media (prefers-color-scheme: dark)': '#1a1821' },
  bgSurface: { default: '#ffffff', '@media (prefers-color-scheme: dark)': '#221f29' },
  bgRaised: { default: '#ffffff', '@media (prefers-color-scheme: dark)': '#292631' },
  bgSubtle: { default: '#f1f0f5', '@media (prefers-color-scheme: dark)': '#302c37' },
  borderDefault: { default: '#d4d0dc', '@media (prefers-color-scheme: dark)': '#3d3a45' },
  borderStrong: { default: '#b4b0bf', '@media (prefers-color-scheme: dark)': '#54515d' },
  borderControl: { default: '#766f85', '@media (prefers-color-scheme: dark)': '#777382' },
  borderOnSolid: {
    default: 'transparent',
    '@media (prefers-color-scheme: dark)': 'rgb(255 255 255 / 0.15)',
  },
  fgDisabled: { default: '#766f85', '@media (prefers-color-scheme: dark)': '#777382' },
  fgMuted: { default: '#554f63', '@media (prefers-color-scheme: dark)': '#9e9ba6' },
  fgSecondary: { default: '#3c3749', '@media (prefers-color-scheme: dark)': '#bdbbc4' },
  fgStrong: { default: '#282334', '@media (prefers-color-scheme: dark)': '#dad8df' },
  fgDefault: { default: '#14101e', '@media (prefers-color-scheme: dark)': '#f2f0f4' },
  fgOnSolid: { default: '#ffffff', '@media (prefers-color-scheme: dark)': '#ffffff' },
  accentSolid: { default: '#7139c6', '@media (prefers-color-scheme: dark)': '#7139c6' },
  accentSolidHover: { default: '#5f2da9', '@media (prefers-color-scheme: dark)': '#824ed0' },
  accentFg: { default: '#5a2ba1', '@media (prefers-color-scheme: dark)': '#bd94f9' },
  accentSubtle: { default: '#f3edfd', '@media (prefers-color-scheme: dark)': '#34244c' },
  focusRing: { default: '#7139c6', '@media (prefers-color-scheme: dark)': '#bd94f9' },
  successFg: { default: '#115f38', '@media (prefers-color-scheme: dark)': '#5ec994' },
  successSubtle: { default: '#e5f5ed', '@media (prefers-color-scheme: dark)': '#203c2e' },
  warningFg: { default: '#814203', '@media (prefers-color-scheme: dark)': '#f0b042' },
  warningSubtle: { default: '#fdefd8', '@media (prefers-color-scheme: dark)': '#40331c' },
  dangerFg: { default: '#a51c18', '@media (prefers-color-scheme: dark)': '#f1827e' },
  dangerSubtle: { default: '#fce9e8', '@media (prefers-color-scheme: dark)': '#41211f' },
  dangerSolid: { default: '#a51c18', '@media (prefers-color-scheme: dark)': '#a72925' },
  infoFg: { default: '#035172', '@media (prefers-color-scheme: dark)': '#5abee2' },
  infoSubtle: { default: '#e5f5fa', '@media (prefers-color-scheme: dark)': '#1c3640' },
  overlayScrim: {
    default: 'rgb(0 0 0 / 0.6)',
    '@media (prefers-color-scheme: dark)': 'rgb(0 0 0 / 0.6)',
  },
  shadowOverlay: {
    default: '0 1px 2px rgb(20 16 30 / 0.08), 0 12px 32px -8px rgb(20 16 30 / 0.22)',
    '@media (prefers-color-scheme: dark)':
      '0 0 0 1px rgb(255 255 255 / 0.08), 0 12px 32px -8px rgb(0 0 0 / 0.55)',
  },
})
