import { describe, expect, it } from 'vitest'
import { delta, fillBuckets, previousRange, rangeFor } from './range'

describe('overview ranges', () => {
  const now = new Date('2026-10-15T12:00:00.000Z')

  it('ends a fixed range now', () => {
    expect(rangeFor('1h', now)).toEqual({
      from: '2026-10-15T11:00:00.000Z',
      to: '2026-10-15T12:00:00.000Z',
    })
    expect(rangeFor('7d', now).from).toBe('2026-10-08T12:00:00.000Z')
  })

  it('reads a custom range as UTC', () => {
    expect(rangeFor('custom', now, { from: '2026-10-01T00:00', to: '2026-10-02T00:00' })).toEqual({
      from: '2026-10-01T00:00:00.000Z',
      to: '2026-10-02T00:00:00.000Z',
    })
  })

  it('compares with the period of equal length just before', () => {
    expect(
      previousRange({ from: '2026-10-15T11:00:00.000Z', to: '2026-10-15T12:00:00.000Z' }),
    ).toEqual({ from: '2026-10-15T10:00:00.000Z', to: '2026-10-15T11:00:00.000Z' })
  })

  it('states the change against the previous period, and nothing without a baseline', () => {
    expect(delta(120, 100)).toBe('+20.0 % vs previous period')
    expect(delta(90, 100)).toBe('-10.0 % vs previous period')
    expect(delta(5, 0)).toBeNull()
    expect(delta(null, 3)).toBeNull()
  })
})

describe('fillBuckets', () => {
  it('puts every step of the range on the axis and keeps the reported buckets', () => {
    const filled = fillBuckets(
      '2026-10-15T10:30:00.000Z',
      '2026-10-15T13:10:00.000Z',
      3600,
      [{ start: '2026-10-15T11:00:00+00:00', figures: 5 }],
      0,
    )
    expect(filled).toEqual([
      { start: '2026-10-15T10:00:00.000Z', figures: 0 },
      { start: '2026-10-15T11:00:00.000Z', figures: 5 },
      { start: '2026-10-15T12:00:00.000Z', figures: 0 },
      { start: '2026-10-15T13:00:00.000Z', figures: 0 },
    ])
  })
})
