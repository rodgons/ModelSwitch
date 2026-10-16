/** The first instant of the current UTC month: Budgets are calendar months in UTC (usage-and-budgets.md section 3). */
export function monthStart(now: Date): string {
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1)).toISOString()
}

/** How much of the Budget the month's Estimated Cost has used, and the tone the gauge shows it in. */
export function budgetUse(
  spent: number,
  limit: number | null | undefined,
): { ratio: number | null; tone: 'success' | 'warning' | 'danger' } {
  if (limit === null || limit === undefined) return { ratio: null, tone: 'success' }
  const ratio = limit === 0 ? (spent > 0 ? 1 : 0) : spent / limit
  return { ratio, tone: ratio >= 1 ? 'danger' : ratio >= 0.8 ? 'warning' : 'success' }
}

/** Model access as typed: comma-separated aliases, or `*` for every alias. */
export function parseAliases(text: string): string[] {
  return text
    .split(',')
    .map((a) => a.trim())
    .filter(Boolean)
}
