export interface DiffLine {
  kind: 'same' | 'added' | 'removed'
  text: string
}

/** A line diff (longest common subsequence) between two Policy Version definitions, for the version history view. */
export function diffLines(before: string, after: string): DiffLine[] {
  const a = before.split('\n')
  const b = after.split('\n')
  const lcs: number[][] = Array.from({ length: a.length + 1 }, () =>
    new Array<number>(b.length + 1).fill(0),
  )
  for (let i = a.length - 1; i >= 0; i--) {
    for (let j = b.length - 1; j >= 0; j--) {
      lcs[i]![j] =
        a[i] === b[j] ? lcs[i + 1]![j + 1]! + 1 : Math.max(lcs[i + 1]![j]!, lcs[i]![j + 1]!)
    }
  }

  const lines: DiffLine[] = []
  let i = 0
  let j = 0
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) {
      lines.push({ kind: 'same', text: a[i]! })
      i++
      j++
    } else if (lcs[i + 1]![j]! >= lcs[i]![j + 1]!) {
      lines.push({ kind: 'removed', text: a[i++]! })
    } else {
      lines.push({ kind: 'added', text: b[j++]! })
    }
  }
  while (i < a.length) lines.push({ kind: 'removed', text: a[i++]! })
  while (j < b.length) lines.push({ kind: 'added', text: b[j++]! })
  return lines
}

/** A definition as the editor and the diff show it: stable, two-space indented JSON. */
export function pretty(definition: unknown): string {
  return JSON.stringify(definition ?? {}, null, 2)
}
