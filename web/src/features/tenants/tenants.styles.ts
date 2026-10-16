import * as stylex from '@stylexjs/stylex'
import { color } from '@/shared/design/color.stylex'
import { radius } from '@/shared/design/space.stylex'

export const gauge = stylex.create({
  track: {
    height: '8px',
    borderRadius: radius.hair,
    backgroundColor: color.bgSubtle,
    overflow: 'hidden',
  },
  fill: (width: string) => ({ height: '100%', width, borderRadius: radius.hair }),
  success: { backgroundColor: color.successFg },
  warning: { backgroundColor: color.warningFg },
  danger: { backgroundColor: color.dangerFg },
})
