import * as stylex from '@stylexjs/stylex'
import { bp } from '@/shared/design/breakpoints.stylex'
import { color } from '@/shared/design/color.stylex'
import { layout } from '@/shared/design/layout.stylex'
import { motion, radius, space } from '@/shared/design/space.stylex'
import { font, text } from '@/shared/design/type.stylex'

// design-system.md sections 7, 8 and 8.1.
export const button = stylex.create({
  base: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: space.s6,
    fontFamily: font.sans,
    fontWeight: text.weightMedium,
    fontSize: text.bodySize,
    lineHeight: text.bodyLine,
    borderRadius: radius.control,
    borderWidth: '1px',
    borderStyle: 'solid',
    borderColor: 'transparent',
    cursor: 'pointer',
    whiteSpace: 'nowrap',
    textDecoration: 'none',
    transitionProperty: 'background-color, border-color, color',
    transitionDuration: motion.fast,
    outline: { default: 'none', ':focus-visible': `2px solid ${color.focusRing}` },
    outlineOffset: '2px',
    transform: { default: null, ':active': { default: 'scale(0.98)', [bp.reducedMotion]: null } },
  },
  sm: { height: '28px', paddingBlock: space.s4, paddingInline: space.s8 },
  md: { height: '32px', paddingBlock: space.s6, paddingInline: space.s12 },
  lg: {
    height: '40px',
    paddingBlock: space.s8,
    paddingInline: space.s16,
    fontSize: text.ledeSize,
    gap: space.s8,
  },
  primary: {
    backgroundColor: { default: color.accentSolid, ':hover': color.accentSolidHover },
    color: color.fgOnSolid,
    borderColor: color.borderOnSolid,
  },
  secondary: {
    backgroundColor: color.bgSubtle,
    color: color.fgDefault,
    borderColor: { default: color.borderDefault, ':hover': color.borderStrong },
  },
  ghost: {
    backgroundColor: { default: 'transparent', ':hover': color.bgSubtle },
    color: { default: color.fgSecondary, ':hover': color.fgDefault },
  },
  danger: {
    backgroundColor: color.dangerSolid,
    color: color.fgOnSolid,
    filter: { default: null, ':hover': 'brightness(0.92)' },
  },
  dangerText: { color: color.dangerFg },
  disabled: { opacity: 0.5, cursor: 'not-allowed' },
})

export const badge = stylex.create({
  base: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: space.s4,
    height: '20px',
    paddingInline: space.s6,
    borderRadius: radius.hair,
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    fontWeight: text.weightMedium,
    letterSpacing: text.trackingCaption,
    whiteSpace: 'nowrap',
  },
  dot: {
    width: '6px',
    height: '6px',
    borderRadius: radius.full,
    backgroundColor: 'currentColor',
    flexShrink: 0,
  },
  success: { color: color.successFg, backgroundColor: color.successSubtle },
  warning: { color: color.warningFg, backgroundColor: color.warningSubtle },
  danger: { color: color.dangerFg, backgroundColor: color.dangerSubtle },
  info: { color: color.infoFg, backgroundColor: color.infoSubtle },
  neutral: { color: color.fgMuted, backgroundColor: color.bgSubtle },
  accent: { color: color.accentFg, backgroundColor: color.accentSubtle },
})

export const typo = stylex.create({
  caption: {
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    fontWeight: text.weightMedium,
    color: color.fgMuted,
  },
  body: { fontSize: text.bodySize, lineHeight: text.bodyLine, color: color.fgDefault },
  lede: {
    fontSize: text.ledeSize,
    lineHeight: text.ledeLine,
    color: color.fgSecondary,
    maxWidth: layout.proseMax,
    margin: 0,
  },
  muted: { color: color.fgMuted },
  mono: { fontFamily: font.mono, fontFeatureSettings: '"zero"', fontSize: text.bodySize },
  numeric: { fontVariantNumeric: 'tabular-nums' },
  title: {
    margin: 0,
    fontSize: text.titleSize,
    lineHeight: text.titleLine,
    fontWeight: text.weightBold,
    color: color.fgDefault,
  },
  error: { color: color.dangerFg },
})

export const link = stylex.create({
  inline: {
    color: color.accentFg,
    textDecorationLine: 'underline',
    textDecorationThickness: { default: '1px', ':hover': '2px' },
    textUnderlineOffset: '3px',
  },
  quiet: {
    color: { default: color.fgSecondary, ':hover': color.fgDefault },
    textDecorationLine: { default: 'none', ':hover': 'underline' },
    textUnderlineOffset: '3px',
  },
  section: { color: color.accentFg, fontWeight: text.weightMedium, textDecoration: 'none' },
  focus: {
    outline: { default: 'none', ':focus-visible': `2px solid ${color.focusRing}` },
    outlineOffset: '2px',
    borderRadius: radius.hair,
  },
})

export const field = stylex.create({
  field: { display: 'flex', flexDirection: 'column', gap: space.s6, minWidth: 0 },
  label: {
    fontSize: text.bodySize,
    lineHeight: text.bodyLine,
    fontWeight: text.weightMedium,
    color: color.fgDefault,
  },
  help: {
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    color: color.fgMuted,
    margin: 0,
  },
  error: {
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    color: color.dangerFg,
    margin: 0,
  },
  control: {
    height: '32px',
    paddingInline: '10px',
    borderRadius: radius.control,
    borderWidth: '1px',
    borderStyle: 'solid',
    borderColor: color.borderControl,
    backgroundColor: color.bgSurface,
    color: color.fgDefault,
    fontFamily: font.sans,
    fontSize: text.bodySize,
    minWidth: 0,
    outline: { default: 'none', ':focus-visible': `2px solid ${color.focusRing}` },
    outlineOffset: '1px',
  },
  textarea: {
    height: 'auto',
    minHeight: '240px',
    paddingBlock: space.s8,
    resize: 'vertical',
    lineHeight: text.bodyLine,
  },
  invalid: { borderColor: color.dangerFg },
  checkbox: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: space.s8,
    fontSize: text.bodySize,
    color: color.fgDefault,
    minHeight: '24px',
  },
})

export const alert = stylex.create({
  base: {
    display: 'flex',
    flexDirection: 'column',
    gap: space.s8,
    paddingBlock: space.s12,
    paddingInline: space.s16,
    borderRadius: radius.surface,
    borderWidth: '1px',
    borderStyle: 'solid',
    fontSize: text.bodySize,
    lineHeight: text.bodyLine,
  },
  danger: {
    backgroundColor: color.dangerSubtle,
    color: color.fgDefault,
    borderColor: `color-mix(in srgb, ${color.dangerFg} 35%, transparent)`,
  },
  warning: {
    backgroundColor: color.warningSubtle,
    color: color.fgDefault,
    borderColor: `color-mix(in srgb, ${color.warningFg} 35%, transparent)`,
  },
  info: {
    backgroundColor: color.infoSubtle,
    color: color.fgDefault,
    borderColor: `color-mix(in srgb, ${color.infoFg} 35%, transparent)`,
  },
  success: {
    backgroundColor: color.successSubtle,
    color: color.fgDefault,
    borderColor: `color-mix(in srgb, ${color.successFg} 35%, transparent)`,
  },
})

export const segmented = stylex.create({
  track: {
    display: 'inline-flex',
    padding: space.s2,
    gap: space.s2,
    backgroundColor: color.bgSubtle,
    borderWidth: '1px',
    borderStyle: 'solid',
    borderColor: color.borderDefault,
    borderRadius: radius.control,
  },
  segment: {
    height: '26px',
    paddingInline: space.s8,
    fontSize: '13px',
    fontWeight: text.weightMedium,
    fontFamily: font.sans,
    color: color.fgSecondary,
    backgroundColor: 'transparent',
    borderWidth: '1px',
    borderStyle: 'solid',
    borderColor: 'transparent',
    borderRadius: radius.hair,
    cursor: 'pointer',
    outline: { default: 'none', ':focus-visible': `2px solid ${color.focusRing}` },
  },
  selected: {
    backgroundColor: color.bgSurface,
    borderColor: color.borderDefault,
    color: color.fgDefault,
  },
})

export const table = stylex.create({
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: text.bodySize,
    lineHeight: text.bodyLine,
    color: color.fgDefault,
  },
  th: {
    textAlign: 'start',
    paddingBlock: space.s8,
    paddingInline: space.s12,
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    fontWeight: text.weightMedium,
    color: color.fgMuted,
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: color.borderDefault,
    whiteSpace: 'nowrap',
  },
  td: {
    paddingBlock: space.s8,
    paddingInline: space.s12,
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: color.borderDefault,
    verticalAlign: 'middle',
  },
  number: { textAlign: 'end', fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
  // Identifiers and error codes: kept on one line; a wide table scrolls inside TableScroll rather than breaking them.
  noWrap: { whiteSpace: 'nowrap' },
  row: { backgroundColor: { default: null, ':hover': color.bgSubtle } },
  highlighted: { backgroundColor: color.accentSubtle },
  // design-system.md section 9.2, TableScroll: the first column stays in view while a wide table scrolls, below lg.
  pinned: {
    position: { default: 'sticky', [bp.lg]: 'static' },
    insetInlineStart: 0,
    zIndex: 1,
    backgroundColor: color.bgSurface,
  },
})

export const state = stylex.create({
  empty: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: space.s12,
    paddingBlock: space.s40,
    textAlign: 'center',
  },
  skeleton: {
    height: '20px',
    borderRadius: radius.hair,
    backgroundColor: color.bgSubtle,
    animationName: stylex.keyframes({
      '0%': { opacity: 1 },
      '50%': { opacity: 0.55 },
      '100%': { opacity: 1 },
    }),
    animationDuration: { default: '1.4s', [bp.reducedMotion]: '0s' },
    animationIterationCount: 'infinite',
  },
})

export const dialog = stylex.create({
  mask: {
    position: 'fixed',
    inset: 0,
    backgroundColor: color.overlayScrim,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    zIndex: layout.zOverlay,
    padding: space.s16,
  },
  root: {
    width: '100%',
    maxWidth: '440px',
    backgroundColor: color.bgRaised,
    color: color.fgDefault,
    borderRadius: radius.dialog,
    boxShadow: color.shadowOverlay,
    padding: space.s24,
    display: 'flex',
    flexDirection: 'column',
    gap: space.s16,
  },
  wide: { maxWidth: '560px' },
  hidden: { display: 'none' },
})

export const tone = stylex.create({
  accent: { color: color.accentFg },
  warning: { color: color.warningFg },
  danger: { color: color.dangerFg },
  success: { color: color.successFg },
})
