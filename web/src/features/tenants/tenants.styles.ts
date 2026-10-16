import * as stylex from '@stylexjs/stylex'
import { color } from '@/shared/design/color.stylex'
import { radius, space } from '@/shared/design/space.stylex'

/** The Budget gauge (design-system.md section 8.1): spent in the status tone, then reserved in a neutral fill, on one track. */
export const gauge = stylex.create({
  track: {
    display: 'flex',
    height: space.s8,
    borderRadius: radius.hair,
    backgroundColor: color.bgSubtle,
    overflow: 'hidden',
  },
  segment: (width: string) => ({ height: '100%', width, flexShrink: 0 }),
  success: { backgroundColor: color.successFg },
  warning: { backgroundColor: color.warningFg },
  danger: { backgroundColor: color.dangerFg },
  reserved: { backgroundColor: color.fgMuted },
  swatch: {
    display: 'inline-block',
    width: space.s8,
    height: space.s8,
    marginInlineEnd: space.s6,
    borderRadius: radius.hair,
  },
})
