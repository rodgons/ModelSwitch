import type { APIRequestContext } from '@playwright/test'
import { expect, owner, signIn, test } from './fixtures'

/**
 * Leaves an Evaluation Lab with data for the screen checks: a free-text Dataset of three Cases, a Prompt and a finished Comparison
 * of two runs on the Mock Provider (alias "balanced" and the pinned Provider Model mock-echo), scored by exact_match. mock-echo
 * repeats the last user message, so every Case passes. The setup goes through the admin API; the test then opens the Comparison
 * in the dashboard.
 */
async function adminApi(request: APIRequestContext) {
  const anonymous = await request.get('/admin/antiforgery-token')
  const session = await request.post('/admin/session', {
    headers: { 'X-XSRF-TOKEN': (await anonymous.json()).token },
    data: { email: owner.email, password: owner.password },
  })
  expect(session.ok(), 'Owner sign-in').toBe(true)
  const headers = { 'X-XSRF-TOKEN': (await session.json()).antiforgeryToken as string }
  return async (method: 'GET' | 'POST', path: string, data?: unknown, contentType?: string) => {
    const response = await request.fetch(path, {
      method,
      headers: contentType ? { ...headers, 'Content-Type': contentType } : headers,
      data: data as string | object | undefined,
    })
    expect(response.ok(), `${method} ${path}: ${await response.text()}`).toBe(true)
    return response.json()
  }
}

test('an Evaluation Run on the mock finishes and its Comparison opens', async ({
  page,
  request,
}) => {
  const api = await adminApi(request)
  const suffix = Date.now()
  const tenants = await api('GET', '/admin/tenants?limit=200')
  const tenantId = tenants.items.find((t: { name: string }) => t.name === 'demo').id
  const providers = await api('GET', '/admin/providers?limit=200')
  const mockId = providers.items.find((p: { name: string }) => p.name === 'mock').id
  const models = await api('GET', `/admin/providers/${mockId}/models?limit=200`)
  const echoId = models.items.find(
    (m: { providerModelId: string }) => m.providerModelId === 'mock-echo',
  ).id

  const dataset = await api('POST', '/admin/datasets', {
    tenantId,
    name: `e2e-echo-${suffix}`,
    taskKind: 'free_text',
  })
  const cases = ['alpha', 'bravo', 'charlie']
    .map((word, index) =>
      JSON.stringify({ id: `c-${index + 1}`, input: { text: word }, expected: word }),
    )
    .join('\n')
  await api('POST', `/admin/datasets/${dataset.id}/versions`, cases, 'application/x-ndjson')
  const prompt = await api('POST', '/admin/prompts', { tenantId, name: `e2e-echo-${suffix}` })
  await api('POST', `/admin/prompts/${prompt.id}/versions`, {
    messages: [{ role: 'user', content: '{{text}}' }],
  })
  const evaluator = { kind: 'exact_match', version: '1' }
  const created = await api('POST', '/admin/evaluations', {
    tenantId,
    datasetId: dataset.id,
    datasetVersion: 1,
    name: `e2e echo ${suffix}`,
    configurations: [
      { target: { alias: 'balanced' }, prompt: { id: prompt.id, version: 1 }, evaluator },
      { target: { providerModelId: echoId }, prompt: { id: prompt.id, version: 1 }, evaluator },
    ],
  })

  for (const runId of created.runIds as string[]) {
    await expect
      .poll(async () => (await api('GET', `/admin/evaluations/${runId}`)).status, {
        timeout: 45_000,
      })
      .not.toMatch(/^(queued|running)$/)
    const run = await api('GET', `/admin/evaluations/${runId}`)
    expect(run.status, JSON.stringify(run.failures)).toBe('succeeded')
    expect(run.aggregates.passed).toBe(3)
  }

  await signIn(page)
  await page.goto(`/comparisons/${created.comparisonId}`)
  await expect(page.getByRole('heading', { level: 1, name: `e2e echo ${suffix}` })).toBeVisible()
})
