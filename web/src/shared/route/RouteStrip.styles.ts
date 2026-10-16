import * as stylex from '@stylexjs/stylex'
import { bp } from '@/shared/design/breakpoints.stylex'
import { color } from '@/shared/design/color.stylex'
import { radius, space } from '@/shared/design/space.stylex'
import { font, text } from '@/shared/design/type.stylex'

const draw = stylex.keyframes({ from: { transform: 'scaleX(0)' }, to: { transform: 'scaleX(1)' } })

// design-system.md section 10: the one bold element of the system.
export const strip = stylex.create({
  root: { display: 'flex', flexDirection: 'column', gap: space.s4, minWidth: 0 },
  track: { position: 'relative', margin: 0, padding: 0, listStyle: 'none' },
  // Inset so the end glyph of a segment that ends the request stays inside the strip.
  full: { height: '44px', width: 'calc(100% - 8px)' },
  mini: { height: '12px', width: '112px', marginInlineEnd: '8px' },
  item: (left: string, width: string) => ({
    position: 'absolute',
    left,
    width,
    top: 0,
    bottom: 0,
    minWidth: '4px',
  }),
  bar: {
    position: 'absolute',
    insetInline: 0,
    borderRadius: radius.hair,
    transformOrigin: 'left center',
    outline: { default: 'none', ':focus-visible': `2px solid ${color.focusRing}` },
    outlineOffset: '2px',
  },
  barFull: { top: '20px', height: '8px' },
  barMini: { top: '3px', height: '6px' },
  drawn: (delay: string) => ({
    animationName: { default: draw, [bp.reducedMotion]: 'none' },
    animationDuration: '240ms',
    animationDelay: delay,
    animationFillMode: 'both',
    animationTimingFunction: 'cubic-bezier(0.2, 0, 0, 1)',
  }),
  succeeded: { backgroundColor: color.successFg, color: color.successFg },
  fellBack: { backgroundColor: color.warningFg, color: color.warningFg },
  failed: { backgroundColor: color.dangerFg, color: color.dangerFg },
  timedOut: { backgroundColor: color.dangerFg, color: color.dangerFg },
  skipped: {
    backgroundImage: `repeating-linear-gradient(45deg, ${color.borderStrong} 0, ${color.borderStrong} 1px, transparent 1px, transparent 3px)`,
    color: color.fgMuted,
    minWidth: '8px',
  },
  emphasised: { boxShadow: `0 0 0 2px ${color.focusRing}` },
  glyph: {
    position: 'absolute',
    right: '-6px',
    fontSize: text.captionSize,
    lineHeight: '12px',
    fontWeight: text.weightBold,
  },
  glyphFull: { top: '18px' },
  glyphMini: { top: 0 },
  label: {
    position: 'absolute',
    top: 0,
    left: 0,
    // Labels may run past a short segment; the strip leaves room by truncating at a fixed width instead.
    maxWidth: '160px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontFamily: font.mono,
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    color: color.fgSecondary,
  },
  labelEnd: { left: 'auto', right: 0 },
  gap: (left: string, width: string) => ({
    position: 'absolute',
    left,
    width,
    borderTopWidth: '1px',
    borderTopStyle: 'dotted',
    borderTopColor: color.borderStrong,
  }),
  gapFull: { top: '24px' },
  gapMini: { top: '6px' },
  ticks: {
    display: 'flex',
    justifyContent: 'space-between',
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    color: color.fgMuted,
    fontVariantNumeric: 'tabular-nums',
  },
})
