import { afterEach, describe, expect, it, vi } from 'vitest'

describe('Case drill-down request', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.resetModules()
  })

  async function load(body: unknown) {
    const fetch = vi.fn(async (request: Request) => {
      void request
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    })
    vi.stubGlobal('fetch', fetch)
    // The client calls same-origin paths; outside a browser, Request needs an origin to resolve them against.
    const BaseRequest = globalThis.Request
    vi.stubGlobal(
      'Request',
      class extends BaseRequest {
        constructor(input: RequestInfo | URL, init?: RequestInit) {
          super(typeof input === 'string' ? new URL(input, 'http://localhost') : input, init)
        }
      },
    )
    const queries = await import('./runQueries')
    return { fetch, queries }
  }

  it("asks the run for one Case's result by its external id, in a single request", async () => {
    const result = { externalId: 'c-7', ordinal: 6, status: 'passed' }
    const { fetch, queries } = await load({ items: [result], nextCursor: null })

    await expect(queries.fetchCaseResult('r1', 'c-7')).resolves.toEqual(result)

    expect(fetch).toHaveBeenCalledTimes(1)
    const url = new URL(fetch.mock.calls[0]![0].url, 'http://x')
    expect(url.pathname).toBe('/admin/evaluations/r1/results')
    expect(url.searchParams.get('externalId')).toBe('c-7')
    expect(url.searchParams.get('limit')).toBe('1')
  })

  it('is null when the run has no result for the Case yet', async () => {
    const { queries } = await load({ items: [], nextCursor: null })

    await expect(queries.fetchCaseResult('r1', 'c-7')).resolves.toBeNull()
  })
})
