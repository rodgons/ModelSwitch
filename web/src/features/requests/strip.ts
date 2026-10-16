import type { Schemas } from '@/api/client'
import type { StripAttempt } from '@/shared/route/routeStrip'

/** "provider/model" as Routing Policies write it, or the id when the model is no longer in the catalog. */
export function modelName(model: Schemas['ModelRef'] | null | undefined): string {
  if (!model) return '—'
  return model.providerName && model.name ? `${model.providerName}/${model.name}` : model.id
}

export function stripAttempts(attempts: Schemas['AttemptTimelineView'][]): StripAttempt[] {
  return attempts.map((a) => ({
    attemptNumber: a.attemptNumber,
    kind: a.kind,
    status: a.status,
    model: modelName(a.model),
    startedAt: a.startedAt,
    completedAt: a.completedAt,
    errorCode: a.errorCode,
    providerErrorType: a.providerErrorType,
  }))
}
