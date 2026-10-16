import * as stylex from '@stylexjs/stylex'
import { color } from '@/shared/design/color.stylex'
import { radius, space } from '@/shared/design/space.stylex'
import { font, text } from '@/shared/design/type.stylex'

/** Case values, scoring schemas and templates: machine text in a quiet mono block (design-system.md section 3.1, font.mono). */
export const code = stylex.create({
  block: {
    margin: 0,
    padding: space.s12,
    overflowX: 'auto',
    fontFamily: font.mono,
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    backgroundColor: color.bgSubtle,
    borderRadius: radius.surface,
    whiteSpace: 'pre',
    maxWidth: '100%',
  },
  wrap: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
  inline: {
    fontFamily: font.mono,
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    whiteSpace: 'nowrap',
  },
  cell: { verticalAlign: 'top' },
})

export const upload = stylex.create({
  file: {
    fontFamily: font.sans,
    fontSize: text.bodySize,
    color: color.fgDefault,
    minHeight: '32px',
    maxWidth: '100%',
    outline: { default: 'none', ':focus-visible': `2px solid ${color.focusRing}` },
    outlineOffset: '2px',
  },
  list: { margin: 0, paddingInlineStart: space.s20 },
  line: { fontVariantNumeric: 'tabular-nums' },
})
