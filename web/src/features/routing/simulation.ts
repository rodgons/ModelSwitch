import type { Schemas } from '@/api/client'
import { formatDuration } from '@/shared/format'

/**
 * The Routing screen's simulate panel (frontend.md section 2): a hypothetical request for
 * `POST /admin/routing-policies/{policyId}/simulate` (api.md section 3, "Routing"), as typed into the form.
 */
export interface SimulationForm {
  /** A Policy Version number, or '' for the latest. */
  version: string
  /** A Tenant id, or '' for none. */
  tenantId: string
  taskType: string
  estimatedInputTokens: string
  maxOutputTokens: string
  streaming: boolean
  jsonObject: boolean
  jsonSchema: boolean
  /** Request parameters the request sends (comma-separated), which a Candidate must declare. */
  parameters: string
}

export function blankSimulation(): SimulationForm {
  return {
    version: '',
    tenantId: '',
    taskType: '',
    estimatedInputTokens: '1000',
    maxOutputTokens: '',
    streaming: false,
    jsonObject: false,
    jsonSchema: false,
    parameters: '',
  }
}

const numberOrNull = (text: string) => {
  const trimmed = text.trim()
  return trimmed === '' || Number.isNaN(Number(trimmed)) ? null : Number(trimmed)
}
const textOrNull = (text: string) => text.trim() || null

/** The request body. Values are passed through as typed, so the server's 400 `validation_failed` names each bad field. */
export function simulationRequest(form: SimulationForm): Schemas['SimulateRoutingRequest'] {
  const requires = [
    ...(form.streaming ? ['streaming'] : []),
    ...(form.jsonObject ? ['jsonObject'] : []),
    ...(form.jsonSchema ? ['jsonSchema'] : []),
    ...form.parameters
      .split(',')
      .map((p) => p.trim())
      .filter(Boolean),
  ]
  return {
    version: numberOrNull(form.version),
    tenantId: textOrNull(form.tenantId),
    taskType: textOrNull(form.taskType),
    requires: [...new Set(requires)],
    estimatedInputTokens: numberOrNull(form.estimatedInputTokens),
    maxOutputTokens: numberOrNull(form.maxOutputTokens),
  }
}

/** `provider/model`, as Routing Policies write Candidates; the id when the Provider Model is no longer in the catalog. */
export function candidateName(candidate: Schemas['SimulatedCandidateView']): string {
  return candidate.providerName && candidate.model
    ? `${candidate.providerName}/${candidate.model}`
    : candidate.providerModelId
}

/** One sentence on what a real request would get. */
export function outcomeSummary(view: Schemas['RoutingSimulationView']): {
  tone: 'success' | 'danger'
  text: string
} {
  if (view.errorCode) {
    const retry =
      view.retryAfterMs === null ? '' : ` Retry after ${formatDuration(view.retryAfterMs)}.`
    return {
      tone: 'danger',
      text: `A request would fail with ${view.errorCode}: ${view.message ?? ''}${retry}`,
    }
  }
  const eligible = view.candidates.filter((c) => c.status === 'eligible')
  const first = eligible[0] ? candidateName(eligible[0]) : 'no Candidate'
  const more = eligible.length - 1
  return {
    tone: 'success',
    text:
      more > 0
        ? `A request would try ${first} first, then ${more} more Candidate${more === 1 ? '' : 's'} as a Fallback.`
        : `A request would try ${first} first.`,
  }
}

export type BadgeTone = 'success' | 'warning' | 'danger' | 'neutral'

/** `eligible` can serve the request, `truncated` is eligible but past `maxCandidates`, `excluded` failed an Eligibility filter. */
export function statusTone(status: string): BadgeTone {
  return status === 'eligible' ? 'success' : status === 'excluded' ? 'danger' : 'neutral'
}

export function circuitTone(state: string): BadgeTone {
  return state === 'closed' ? 'success' : state === 'half_open' ? 'warning' : 'danger'
}

/** Labels shown on the badges, in sentence case (design-system.md section 12). */
export const statusLabel: Record<string, string> = {
  eligible: 'Eligible',
  truncated: 'Beyond max Candidates',
  excluded: 'Excluded',
}

export const circuitLabel: Record<string, string> = {
  closed: 'Closed',
  half_open: 'Half-open',
  open: 'Open',
}
