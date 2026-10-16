/**
 * The aggregates of a run (api.md section 3, `RunAggregates`; evaluation.md section 3), as far as the dashboard reads them. The
 * OpenAPI document types them as plain JSON, so this is the one place their shape is written down on the client.
 */
export interface RunAggregates {
  evaluatorKind: string
  casesTotal: number
  scored: number
  passed: number
  failed: number
  errored: number
  skipped: number
  erroredRate: number | null
  quality: { metric: string; value: number | null }
  classification?: {
    perClass: { label: string; precision: number; recall: number; f1: number; support: number }[]
    macroF1: number | null
    confusion: { rows: string[]; columns: string[]; counts: number[][] }
  }
  jsonFields?: { perFieldAccuracy: Record<string, number>; parseFailureRate: number | null }
  llmJudge?: { distribution: { from: number; to: number; count: number }[]; modelJudged: boolean }
  latency: { p50Ms: number | null; p95Ms: number | null }
  tokens: {
    candidateInput: number
    candidateOutput: number
    judgeInput: number
    judgeOutput: number
  }
  cost: { candidate: number; judge: number; total: number; perCaseCandidate: number | null }
}

export const qualityMetricLabel = (metric: string) =>
  ({ accuracy: 'Accuracy', meanScore: 'Mean score', meanTokenF1: 'Mean token F1' })[metric] ??
  metric
