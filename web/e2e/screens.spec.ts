import type { Page } from '@playwright/test'
import { expect, expectAccessible, settled, signIn, test } from './fixtures'

/**
 * An axe pass (WCAG 2.1 A and AA) over each main screen, in the light and the dark theme, with the data scripts/seed-demo.sh and
 * the smoke test leave behind. Every screen also runs under the CSP check of the fixtures.
 */
type Screen = { name: string; path: (page: Page) => Promise<string>; heading: string | RegExp }

async function firstId(page: Page, path: string, pick: (item: Record<string, unknown>) => boolean) {
  const response = await page.request.get(path)
  expect(response.ok(), `GET ${path}`).toBe(true)
  const items = ((await response.json()) as { items: Record<string, unknown>[] }).items
  const item = items.find(pick)
  expect(item, `an item of GET ${path}`).toBeDefined()
  return String(item!.id)
}

const fixed = (path: string) => async () => path

const screens: Screen[] = [
  { name: 'Overview', path: fixed('/'), heading: 'Overview' },
  { name: 'Requests', path: fixed('/requests'), heading: 'Requests' },
  {
    name: 'Request detail',
    path: async (page) => `/requests/${await firstId(page, '/admin/requests?limit=1', () => true)}`,
    heading: /.+/,
  },
  { name: 'Providers', path: fixed('/providers'), heading: 'Providers' },
  {
    name: 'Provider Models',
    path: async (page) =>
      `/providers/${await firstId(page, '/admin/providers?limit=200', (p) => p.name === 'mock')}/models`,
    heading: /mock/,
  },
  { name: 'Routing', path: fixed('/routing'), heading: 'Routing' },
  { name: 'Tenants', path: fixed('/tenants'), heading: 'Tenants' },
  {
    name: 'Tenant detail',
    path: async (page) =>
      `/tenants/${await firstId(page, '/admin/tenants?limit=200', (t) => t.name === 'demo')}`,
    heading: 'demo',
  },
  {
    name: 'Evaluations: Datasets',
    path: fixed('/evaluations?tab=datasets'),
    heading: 'Evaluations',
  },
  { name: 'Evaluations: Prompts', path: fixed('/evaluations?tab=prompts'), heading: 'Evaluations' },
  {
    name: 'Evaluations: Comparisons',
    path: fixed('/evaluations?tab=comparisons'),
    heading: 'Evaluations',
  },
  { name: 'Run builder', path: fixed('/evaluations/new'), heading: 'Start a run' },
  // The Evaluation Lab data comes from evaluation.spec.ts.
  {
    name: 'Dataset',
    path: async (page) =>
      `/evaluations/datasets/${await firstId(page, '/admin/datasets?limit=1', () => true)}`,
    heading: /^e2e-echo-/,
  },
  {
    name: 'Dataset Version',
    path: async (page) =>
      `/evaluations/datasets/${await firstId(page, '/admin/datasets?limit=1', () => true)}/versions/1`,
    heading: /.+/,
  },
  {
    name: 'Prompt',
    path: async (page) =>
      `/evaluations/prompts/${await firstId(page, '/admin/prompts?limit=1', () => true)}`,
    heading: /^e2e-echo-/,
  },
  {
    name: 'Comparison',
    path: async (page) =>
      `/comparisons/${await firstId(page, '/admin/evaluations?limit=1', () => true)}`,
    heading: /^e2e echo /,
  },
  {
    name: 'Evaluation Run',
    path: async (page) => {
      const response = await page.request.get('/admin/evaluations?limit=1')
      const comparison = (await response.json()).items[0] as { runs: { id: string }[] }
      return `/evaluations/runs/${comparison.runs[0]!.id}`
    },
    heading: /.+/,
  },
]

for (const colorScheme of ['light', 'dark'] as const) {
  test.describe(`${colorScheme} theme`, () => {
    test.use({ colorScheme })

    test('sign-in screen', async ({ page }) => {
      await page.goto('/sign-in')
      await expect(
        page.getByRole('heading', { level: 1, name: 'Sign in to ModelSwitch' }),
      ).toBeVisible()
      await expectAccessible(page, `Sign in (${colorScheme})`)
    })

    for (const screen of screens) {
      test(screen.name, async ({ page }) => {
        await signIn(page)
        await page.goto(await screen.path(page))
        await expect(page.getByRole('heading', { level: 1, name: screen.heading })).toBeVisible()
        await settled(page)
        await expectAccessible(page, `${screen.name} (${colorScheme})`)
      })
    }
  })
}
