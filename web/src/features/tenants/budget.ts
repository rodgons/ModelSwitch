/** The first instant of the current UTC month: Budgets are calendar months in UTC (usage-and-budgets.md section 3). */
export function monthStart(now: Date): string {
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1)).toISOString()
}

export interface BudgetUse {
  /** (spent + reserved) / limit: what the next Reservation is checked against. Null without a Budget. */
  ratio: number | null
  /** The gauge's first segment: settled Estimated Cost, as a share of the track (0 to 1). */
  spentShare: number
  /** The second segment: held by requests in flight, drawn after spent and never past the end of the track. */
  reservedShare: number
  tone: 'success' | 'warning' | 'danger'
}

/**
 * How much of the current Budget period is used (usage-and-budgets.md section 3): spent and reserved together, because a request is
 * refused when spent + reserved + its hold would pass the limit. A zero Budget is used up as soon as anything is spent or held.
 */
export function budgetUse(
  spent: number,
  reserved: number,
  limit: number | null | undefined,
): BudgetUse {
  if (limit === null || limit === undefined) {
    return { ratio: null, spentShare: 0, reservedShare: 0, tone: 'success' }
  }
  const share = (amount: number) => (limit === 0 ? (amount > 0 ? 1 : 0) : amount / limit)
  const ratio = limit === 0 ? share(spent + reserved) : (spent + reserved) / limit
  const spentShare = Math.min(1, share(spent))
  const reservedShare = Math.min(1 - spentShare, share(reserved))
  return {
    ratio,
    spentShare,
    reservedShare,
    tone: ratio >= 1 ? 'danger' : ratio >= 0.8 ? 'warning' : 'success',
  }
}

/** Model access as typed: comma-separated aliases, or `*` for every alias. */
export function parseAliases(text: string): string[] {
  return text
    .split(',')
    .map((a) => a.trim())
    .filter(Boolean)
}
