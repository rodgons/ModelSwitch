import { describe, expect, it } from 'vitest'
import type { Schemas } from '@/api/client'
import {
  blankSimulation,
  candidateName,
  circuitTone,
  outcomeSummary,
  simulationRequest,
  statusTone,
} from './simulation'

const candidate = (
  overrides: Partial<Schemas['SimulatedCandidateView']> = {},
): Schemas['SimulatedCandidateView'] => ({
  providerModelId: '0192f0c8-0000-7000-8000-000000000001',
  providerId: null,
  providerName: 'openai-main',
  model: 'gpt-4.1-mini',
  status: 'eligible',
  rank: 1,
  exclusionReason: null,
  circuitState: 'closed',
  worstCaseEstimatedCost: 0.002,
  ...overrides,
})

const view = (
  overrides: Partial<Schemas['RoutingSimulationView']> = {},
): Schemas['RoutingSimulationView'] => ({
  policyId: 'p',
  policyName: 'balanced',
  version: 3,
  strategy: 'cost',
  ruleMatched: null,
  effectiveMaxOutputTokens: 1024,
  errorCode: null,
  message: null,
  retryAfterMs: null,
  candidates: [candidate()],
  currency: 'USD',
  ...overrides,
})

describe('routing simulation form', () => {
  it('sends numbers, leaves empty optional fields out as null and picks the latest version by default', () => {
    expect(simulationRequest({ ...blankSimulation(), estimatedInputTokens: ' 1200 ' })).toEqual({
      version: null,
      tenantId: null,
      taskType: null,
      requires: [],
      estimatedInputTokens: 1200,
      maxOutputTokens: null,
    })
  })

  it('combines the capability checkboxes with typed parameter names, without duplicates', () => {
    const request = simulationRequest({
      ...blankSimulation(),
      streaming: true,
      jsonSchema: true,
      parameters: 'temperature, seed,, temperature',
      maxOutputTokens: '256',
      taskType: ' extraction ',
      tenantId: 't-1',
      version: '2',
    })
    expect(request.requires).toEqual(['streaming', 'jsonSchema', 'temperature', 'seed'])
    expect(request).toMatchObject({
      maxOutputTokens: 256,
      taskType: 'extraction',
      tenantId: 't-1',
      version: 2,
    })
  })

  it('passes a non-numeric token count through so the server reports it as a field error', () => {
    expect(
      simulationRequest({ ...blankSimulation(), estimatedInputTokens: '' }).estimatedInputTokens,
    ).toBeNull()
  })
})

describe('routing simulation result', () => {
  it('names a Candidate provider/model, or by id when it left the catalog', () => {
    expect(candidateName(candidate())).toBe('openai-main/gpt-4.1-mini')
    expect(candidateName(candidate({ providerName: null, model: null }))).toBe(
      '0192f0c8-0000-7000-8000-000000000001',
    )
  })

  it('says which Candidate is tried first, or the error a request would get', () => {
    expect(outcomeSummary(view())).toEqual({
      tone: 'success',
      text: 'A request would try openai-main/gpt-4.1-mini first.',
    })
    expect(
      outcomeSummary(view({ candidates: [candidate(), candidate({ rank: 2, model: 'gpt-4.1' })] }))
        .text,
    ).toBe(
      'A request would try openai-main/gpt-4.1-mini first, then 1 more Candidate as a Fallback.',
    )
    const failed = outcomeSummary(
      view({ errorCode: 'no_eligible_model', message: 'No eligible model.', candidates: [] }),
    )
    expect(failed.tone).toBe('danger')
    expect(failed.text).toContain('no_eligible_model')
    expect(
      outcomeSummary(
        view({ errorCode: 'provider_unavailable', message: 'All open.', retryAfterMs: 1500 }),
      ).text,
    ).toContain('1.50 s')
  })

  it('colors status and circuit state, always with a text label beside them', () => {
    expect(statusTone('eligible')).toBe('success')
    expect(statusTone('truncated')).toBe('neutral')
    expect(statusTone('excluded')).toBe('danger')
    expect(circuitTone('closed')).toBe('success')
    expect(circuitTone('half_open')).toBe('warning')
    expect(circuitTone('open')).toBe('danger')
  })
})
