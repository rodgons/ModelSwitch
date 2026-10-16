import { describe, expect, it } from 'vitest'
import { budgetUse, monthStart, parseAliases } from './budget'

describe('tenant budget gauge', () => {
  it('starts the month at midnight UTC on the first', () => {
    expect(monthStart(new Date('2026-10-15T23:30:00-05:00'))).toBe('2026-10-01T00:00:00.000Z')
  })

  it('warns from 80 % and turns red at the limit', () => {
    expect(budgetUse(50, 100)).toEqual({ ratio: 0.5, tone: 'success' })
    expect(budgetUse(80, 100).tone).toBe('warning')
    expect(budgetUse(100, 100).tone).toBe('danger')
  })

  it('has no ratio without a Budget', () => {
    expect(budgetUse(12, null)).toEqual({ ratio: null, tone: 'success' })
  })

  it('reads model access as a comma-separated list', () => {
    expect(parseAliases(' balanced, local ,, ')).toEqual(['balanced', 'local'])
    expect(parseAliases('*')).toEqual(['*'])
  })
})
