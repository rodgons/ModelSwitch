import createClient, { type Middleware } from 'openapi-fetch'
import type { components, paths } from './schema'

export type Schemas = components['schemas']

/**
 * An admin API error, read from its Problem Details body (api.md section 2). Error states show `errorCode` and `requestId`
 * (design-system.md section 12), and forms map `fieldErrors` onto their fields.
 */
export class ApiError extends Error {
  readonly status: number
  readonly errorCode: string
  readonly requestId: string | null
  readonly fieldErrors: Record<string, string[]>

  constructor(
    status: number,
    errorCode: string,
    message: string,
    requestId: string | null,
    fieldErrors: Record<string, string[]> = {},
  ) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.errorCode = errorCode
    this.requestId = requestId
    this.fieldErrors = fieldErrors
  }

  static async from(response: Response): Promise<ApiError> {
    let body: Record<string, unknown> = {}
    try {
      body = (await response.clone().json()) as Record<string, unknown>
    } catch {
      // Not Problem Details (a proxy error page, for example): keep the status.
    }
    const text = (key: string) => (typeof body[key] === 'string' ? (body[key] as string) : null)
    return new ApiError(
      response.status,
      text('errorCode') ?? `http_${response.status}`,
      text('detail') ?? text('title') ?? response.statusText,
      text('requestId') ?? response.headers.get('X-Request-Id'),
      (body.errors as Record<string, string[]> | undefined) ?? {},
    )
  }
}

let antiforgeryToken: string | null = null

/** The token every state-changing admin request must carry in X-XSRF-TOKEN (security.md section 3). */
export function setAntiforgeryToken(token: string | null) {
  antiforgeryToken = token
}

/** Called when the session has ended (a 401 from any admin call), so the app can return to sign-in. */
let onUnauthenticated: () => void = () => {}
export function whenUnauthenticated(handler: () => void) {
  onUnauthenticated = handler
}

const safeMethods = new Set(['GET', 'HEAD', 'OPTIONS'])

const middleware: Middleware = {
  onRequest({ request }) {
    if (!safeMethods.has(request.method) && antiforgeryToken) {
      request.headers.set('X-XSRF-TOKEN', antiforgeryToken)
    }
    return request
  },
  async onResponse({ request, response }) {
    if (response.ok) return response
    const error = await ApiError.from(response)
    if (error.status === 401 && !request.url.endsWith('/admin/session')) onUnauthenticated()
    throw error
  },
}

export const api = createClient<paths>({ baseUrl: '', credentials: 'same-origin' })
api.use(middleware)

/** Unwraps an openapi-fetch result: errors were already thrown by the middleware, so data is present on success. */
export function data<T>(result: { data?: T }): T {
  return result.data as T
}
