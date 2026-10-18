import * as stylex from '@stylexjs/stylex'
import { bp } from '@/shared/design/breakpoints.stylex'
import { color } from '@/shared/design/color.stylex'
import { layout } from '@/shared/design/layout.stylex'
import { radius, space } from '@/shared/design/space.stylex'
import { text } from '@/shared/design/type.stylex'

// design-system.md section 9.2. Feature components never write their own grid or media query; they compose these.
export const primitives = stylex.create({
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  inline: { display: 'flex', flexDirection: 'row', flexWrap: 'wrap', minWidth: 0 },
  noWrap: { flexWrap: 'nowrap' },
  alignStart: { alignItems: 'flex-start' },
  alignCenter: { alignItems: 'center' },
  alignBaseline: { alignItems: 'baseline' },
  alignEnd: { alignItems: 'flex-end' },
  justifyStart: { justifyContent: 'flex-start' },
  justifyBetween: { justifyContent: 'space-between' },
  justifyEnd: { justifyContent: 'flex-end' },
  grid: (min: string) => ({
    display: 'grid',
    gridTemplateColumns: `repeat(auto-fit, minmax(min(${min}, 100%), 1fr))`,
  }),

  container: {
    width: '100%',
    marginInline: 'auto',
    paddingInline: { default: layout.gutterSm, [bp.md]: layout.gutterMd, [bp.xl]: layout.gutterXl },
    paddingBlock: { default: space.s24, [bp.md]: space.s32 },
  },
  page: { maxWidth: layout.pageMax },
  wide: { maxWidth: 'none' },
  form: { maxWidth: `calc(${layout.formMax} + 2 * ${layout.gutterXl})` },

  split: { display: 'grid', gap: space.s24, gridTemplateColumns: 'minmax(0, 1fr)' },
  splitAtLg: {
    gridTemplateColumns: { default: 'minmax(0, 1fr)', [bp.lg]: `minmax(0, 1fr) ${layout.aside}` },
  },
  splitAtXl: {
    gridTemplateColumns: { default: 'minmax(0, 1fr)', [bp.xl]: `minmax(0, 1fr) ${layout.aside}` },
  },

  pageHeader: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: space.s16,
    marginBottom: space.s24,
  },
  back: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: space.s4,
    minHeight: '24px',
    marginBottom: space.s4,
    marginInlineStart: '-2px',
    fontSize: text.bodySize,
    lineHeight: text.bodyLine,
  },
  // With a back link above the title, the actions move down by the link's height so they line up with the title.
  actionsBelowBack: { marginTop: { default: 0, [bp.sm]: `calc(24px + ${space.s4})` } },
  heading: {
    margin: 0,
    fontSize: text.headingSize,
    lineHeight: text.headingLine,
    fontWeight: text.weightBold,
    letterSpacing: text.trackingHeading,
    color: color.fgDefault,
  },
  lede: {
    margin: 0,
    marginTop: space.s4,
    maxWidth: layout.proseMax,
    fontSize: text.ledeSize,
    lineHeight: text.ledeLine,
    color: color.fgSecondary,
  },

  section: { display: 'flex', flexDirection: 'column', gap: space.s12, minWidth: 0 },
  sectionTitle: {
    margin: 0,
    fontSize: text.titleSize,
    lineHeight: text.titleLine,
    fontWeight: text.weightBold,
    letterSpacing: text.trackingTitle,
    color: color.fgDefault,
  },
  surface: {
    backgroundColor: color.bgSurface,
    borderWidth: '1px',
    borderStyle: 'solid',
    borderColor: color.borderDefault,
    borderRadius: radius.surface,
    padding: space.s16,
    minWidth: 0,
  },
  flush: { padding: 0, overflow: 'hidden' },

  // Positioned, so absolutely positioned descendants (VisuallyHidden labels, tooltips) scroll with it instead of widening the page.
  tableScroll: {
    position: 'relative',
    overflowX: 'auto',
    maxWidth: '100%',
    outline: { default: 'none', ':focus-visible': `2px solid ${color.focusRing}` },
    outlineOffset: '2px',
  },
  visuallyHidden: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    padding: 0,
    margin: '-1px',
    overflow: 'hidden',
    clip: 'rect(0, 0, 0, 0)',
    whiteSpace: 'nowrap',
    borderWidth: 0,
  },

  facts: { display: 'flex', flexWrap: 'wrap', columnGap: space.s24, rowGap: space.s8, margin: 0 },
  fact: { display: 'flex', flexDirection: 'column', gap: space.s2, minWidth: 0 },
  factLabel: {
    margin: 0,
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    fontWeight: text.weightMedium,
    color: color.fgMuted,
  },
  factValue: {
    margin: 0,
    fontSize: text.bodySize,
    lineHeight: text.bodyLine,
    color: color.fgDefault,
    overflowWrap: 'anywhere',
  },

  readout: {
    display: 'grid',
    gridTemplateColumns: {
      default: 'repeat(2, minmax(0, 1fr))',
      [bp.sm]: 'repeat(3, minmax(0, 1fr))',
      [bp.lg]: 'repeat(auto-fit, minmax(0, 1fr))',
    },
    borderBlockWidth: '1px',
    borderBlockStyle: 'solid',
    borderColor: color.borderDefault,
  },
  readoutItem: {
    display: 'flex',
    flexDirection: 'column',
    gap: space.s4,
    padding: space.s16,
    minWidth: 0,
    borderInlineStartWidth: '1px',
    borderInlineStartStyle: 'solid',
    borderInlineStartColor: color.borderDefault,
  },
  readoutLabel: {
    display: 'flex',
    alignItems: 'center',
    gap: space.s6,
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    fontWeight: text.weightMedium,
    color: color.fgMuted,
  },
  readoutValue: {
    fontSize: text.headingSize,
    lineHeight: text.headingLine,
    fontWeight: text.weightMedium,
    letterSpacing: text.trackingTitle,
    fontVariantNumeric: 'tabular-nums',
    color: color.fgDefault,
  },
  reset: { margin: 0 },
  readoutDelta: {
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    color: color.fgMuted,
    fontVariantNumeric: 'tabular-nums',
  },
})
