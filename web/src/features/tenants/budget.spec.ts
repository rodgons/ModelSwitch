import { describe, expect, it } from 'vitest'
import { budgetUse, monthStart, parseAliases } from './budget'

describe('tenant budget gauge', () => {
  it('starts the month at midnight UTC on the first', () => {
    expect(monthStart(new Date('2026-10-15T23:30:00-05:00'))).toBe('2026-10-01T00:00:00.000Z')
  })

  it('warns from 80 % and turns red at the limit, counting reserved with spent', () => {
    expect(budgetUse(50, 0, 100)).toMatchObject({ ratio: 0.5, tone: 'success' })
    expect(budgetUse(70, 10, 100).tone).toBe('warning')
    expect(budgetUse(90, 10, 100).tone).toBe('danger')
  })

  it('draws spent then reserved as two segments that never pass the full track', () => {
    expect(budgetUse(40, 10, 100)).toMatchObject({
      spentShare: 0.4,
      reservedShare: 0.1,
      ratio: 0.5,
    })
    expect(budgetUse(90, 30, 100)).toMatchObject({
      spentShare: 0.9,
      reservedShare: expect.closeTo(0.1),
      ratio: 1.2,
    })
    expect(budgetUse(150, 5, 100)).toMatchObject({ spentShare: 1, reservedShare: 0 })
  })

  it('treats a zero Budget as used up as soon as anything is spent or held', () => {
    expect(budgetUse(0, 0, 0)).toMatchObject({ ratio: 0, tone: 'success' })
    expect(budgetUse(0, 0.01, 0)).toMatchObject({ ratio: 1, tone: 'danger', reservedShare: 1 })
  })

  it('has no ratio without a Budget', () => {
    expect(budgetUse(12, 3, null)).toEqual({
      ratio: null,
      spentShare: 0,
      reservedShare: 0,
      tone: 'success',
    })
  })

  it('reads model access as a comma-separated list', () => {
    expect(parseAliases(' balanced, local ,, ')).toEqual(['balanced', 'local'])
    expect(parseAliases('*')).toEqual(['*'])
  })
})
