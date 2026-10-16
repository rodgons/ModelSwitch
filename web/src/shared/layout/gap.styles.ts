import * as stylex from '@stylexjs/stylex'
import { space } from '@/shared/design/space.stylex'

/** The spacing steps a primitive's `gap` prop accepts (design-system.md section 4). */
export type Gap = 0 | 2 | 4 | 6 | 8 | 12 | 16 | 20 | 24 | 32 | 40 | 48 | 64

export const gaps = stylex.create({
  0: { gap: space.s0 },
  2: { gap: space.s2 },
  4: { gap: space.s4 },
  6: { gap: space.s6 },
  8: { gap: space.s8 },
  12: { gap: space.s12 },
  16: { gap: space.s16 },
  20: { gap: space.s20 },
  24: { gap: space.s24 },
  32: { gap: space.s32 },
  40: { gap: space.s40 },
  48: { gap: space.s48 },
  64: { gap: space.s64 },
})
