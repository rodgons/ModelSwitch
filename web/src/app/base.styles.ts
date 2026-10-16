import * as stylex from '@stylexjs/stylex'
import { color } from '@/shared/design/color.stylex'
import { font, text } from '@/shared/design/type.stylex'

// The document body: the canvas and the base type, so text outside a component still uses the tokens.
export const base = stylex.create({
  body: {
    margin: 0,
    backgroundColor: color.bgCanvas,
    color: color.fgDefault,
    fontFamily: font.sans,
    fontSize: text.bodySize,
    lineHeight: text.bodyLine,
    WebkitFontSmoothing: 'antialiased',
  },
})
