import { describe, expect, it } from 'vitest'
import {
  formatDuration,
  formatMoney,
  formatPercent,
  formatRelative,
  formatScore,
  formatTokens,
  truncateMiddle,
} from './index'

// frontend.md section 3, "Units": the only place figures are formatted.
describe('formatDuration', () => {
  it('shows milliseconds below one second and seconds with two decimals above', () => {
    expect(formatDuration(0)).toBe('0 ms')
    expect(formatDuration(812)).toBe('812 ms')
    expect(formatDuration(999)).toBe('999 ms')
    expect(formatDuration(1000)).toBe('1.00 s')
    expect(formatDuration(1843)).toBe('1.84 s')
  })

  it('shows a dash when the duration is unknown', () => {
    expect(formatDuration(null)).toBe('—')
  })
})

describe('formatTokens', () => {
  it('uses thousands separators', () => {
    expect(formatTokens(12480)).toBe('12,480')
    expect(formatTokens(0)).toBe('0')
    expect(formatTokens(null)).toBe('—')
  })
})

describe('formatMoney', () => {
  it('keeps four significant decimals below one dollar', () => {
    expect(formatMoney(0.0000213)).toBe('$0.00002130')
    expect(formatMoney(0.0021)).toBe('$0.002100')
    expect(formatMoney(0.5)).toBe('$0.5000')
  })

  it('uses two decimals from one dollar and thousands separators', () => {
    expect(formatMoney(41.07)).toBe('$41.07')
    expect(formatMoney(1234.5)).toBe('$1,234.50')
  })

  it('shows zero plainly and a dash for unknown', () => {
    expect(formatMoney(0)).toBe('$0.00')
    expect(formatMoney(null)).toBe('—')
  })
})

describe('formatPercent', () => {
  it('shows two decimals and a dash for no data', () => {
    expect(formatPercent(0.9931)).toBe('99.31 %')
    expect(formatPercent(0.021)).toBe('2.10 %')
    expect(formatPercent(null)).toBe('—')
  })
})

describe('truncateMiddle', () => {
  it('keeps the start and the end of a long identifier', () => {
    expect(truncateMiddle('0192f0c8-1234-7000-8000-00000000f3a1', 8, 4)).toBe('0192f0c8…f3a1')
  })

  it('leaves a short identifier alone', () => {
    expect(truncateMiddle('msk_3f9a2k7q', 8, 4)).toBe('msk_3f9a2k7q')
  })
})

describe('formatRelative', () => {
  const now = new Date('2026-10-15T12:00:00Z')

  it('says how long ago something happened', () => {
    expect(formatRelative('2026-10-15T11:59:30Z', now)).toBe('30 s ago')
    expect(formatRelative('2026-10-15T11:58:00Z', now)).toBe('2 min ago')
    expect(formatRelative('2026-10-15T09:00:00Z', now)).toBe('3 h ago')
    expect(formatRelative('2026-10-12T12:00:00Z', now)).toBe('3 d ago')
  })
})

describe('formatScore', () => {
  it('shows a 0–1 Evaluator score with three decimals', () => {
    expect(formatScore(0.8567)).toBe('0.857')
    expect(formatScore(1)).toBe('1.000')
    expect(formatScore(null)).toBe('—')
  })
})
