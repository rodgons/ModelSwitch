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
    // The parts the browser draws: native checkboxes and radios, the text caret and scrollbars. All three inherit.
    accentColor: color.accentSolid,
    caretColor: color.accentFg,
    scrollbarColor: `${color.borderStrong} transparent`,
  },
})
