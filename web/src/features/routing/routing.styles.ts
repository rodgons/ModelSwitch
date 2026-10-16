import * as stylex from '@stylexjs/stylex'
import { color } from '@/shared/design/color.stylex'
import { radius, space } from '@/shared/design/space.stylex'
import { font, text } from '@/shared/design/type.stylex'

export const diff = stylex.create({
  block: {
    margin: 0,
    padding: space.s12,
    overflowX: 'auto',
    fontFamily: font.mono,
    fontSize: text.captionSize,
    lineHeight: text.captionLine,
    backgroundColor: color.bgSubtle,
    borderRadius: radius.surface,
  },
  line: { display: 'block', whiteSpace: 'pre' },
  added: { backgroundColor: color.successSubtle, color: color.successFg },
  removed: { backgroundColor: color.dangerSubtle, color: color.dangerFg },
})
