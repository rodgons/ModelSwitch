import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@/api/client'
import RouteStrip from '@/shared/route/RouteStrip.vue'
import Money from './Money.vue'
import QueryView from './QueryView.vue'
import StatusBadge from './StatusBadge.vue'

// frontend.md section 3: the UX rules every screen relies on.
describe('QueryView', () => {
  const view = (props: Record<string, unknown>) =>
    mount(QueryView, {
      props: { isPending: false, error: null, data: undefined, ...props },
      slots: { default: '<p>the data</p>', empty: '<button>Add one</button>' },
    })

  it('shows a skeleton while loading', () => {
    expect(view({ isPending: true }).text()).toContain('Loading')
  })

  it('shows the error code and the request id on failure', () => {
    const text = view({
      error: new ApiError(
        503,
        'provider_unavailable',
        'The Provider is temporarily unavailable.',
        'req-123',
      ),
    }).text()
    expect(text).toContain('provider_unavailable')
    expect(text).toContain('req-123')
  })

  it('explains an empty result and offers the next action', () => {
    const wrapper = view({
      data: [],
      isEmpty: (d: unknown[]) => d.length === 0,
      emptyText: 'No Providers yet.',
    })
    expect(wrapper.text()).toContain('No Providers yet.')
    expect(wrapper.find('button').text()).toBe('Add one')
  })

  it('renders the data otherwise', () => {
    expect(view({ data: [1] }).text()).toBe('the data')
  })
})

describe('Money', () => {
  it('always labels an Estimated Cost as estimated', () => {
    expect(mount(Money, { props: { amount: 0.0021, estimated: true } }).text()).toBe(
      '$0.002100 Estimated',
    )
    expect(mount(Money, { props: { amount: 0.0021, estimated: true, badge: false } }).text()).toBe(
      '$0.002100 (estimated)',
    )
  })

  it('shows a Price without the label', () => {
    expect(mount(Money, { props: { amount: 2.5, estimated: false } }).text()).toBe('$2.50')
  })
})

describe('StatusBadge', () => {
  it('names the state in text, never by color alone', () => {
    expect(mount(StatusBadge, { props: { status: 'skipped_circuit_open' } }).text()).toBe(
      'Skipped, circuit open',
    )
    expect(mount(StatusBadge, { props: { status: 'failed' } }).text()).toBe('Failed')
  })
})

describe('RouteStrip', () => {
  it('is an ordered list of Attempts with accessible names', () => {
    const wrapper = mount(RouteStrip, {
      props: {
        variant: 'mini',
        startedAt: '2026-10-15T12:00:00.000Z',
        durationMs: 900,
        attempts: [
          {
            attemptNumber: 1,
            kind: 'primary',
            status: 'failed',
            model: 'openai/gpt-4o-mini',
            startedAt: '2026-10-15T12:00:00.000Z',
            completedAt: '2026-10-15T12:00:00.812Z',
            errorCode: null,
            providerErrorType: null,
          },
          {
            attemptNumber: 2,
            kind: 'fallback',
            status: 'succeeded',
            model: 'ollama/llama3.1:8b',
            startedAt: '2026-10-15T12:00:00.812Z',
            completedAt: '2026-10-15T12:00:00.900Z',
            errorCode: null,
            providerErrorType: null,
          },
        ],
      },
    })
    const names = wrapper
      .findAll('ol [role="img"]')
      .map((segment) => segment.attributes('aria-label'))
    expect(names).toEqual([
      'Attempt 1, openai/gpt-4o-mini, failed after 812 ms, fell back',
      'Attempt 2, ollama/llama3.1:8b, succeeded after 88 ms',
    ])
  })
})
