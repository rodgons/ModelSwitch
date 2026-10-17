import { defineConfig, devices } from '@playwright/test'

// The smoke E2E and axe suite (docs/testing.md section 1). It runs against the Compose stack that scripts/e2e.sh starts and seeds,
// which also passes the proxy URL and the Owner's credentials in E2E_BASE_URL, E2E_OWNER_EMAIL and E2E_OWNER_PASSWORD.
const chromium = {
  ...devices['Desktop Chrome'],
  channel: 'chromium',
  viewport: { width: 1440, height: 900 },
}

export default defineConfig({
  testDir: './e2e',
  // The tests share one database; the smoke flow creates the data the screen checks look at, so they run in order.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: process.env.CI
    ? [['list'], ['html', { open: 'never' }], ['github']]
    : [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:8081',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  // The screen checks need the request the smoke flow sends, so they depend on it. Chromium only: the suite checks the product,
  // not browser differences.
  projects: [
    { name: 'smoke', testMatch: ['smoke.spec.ts', 'evaluation.spec.ts'], use: chromium },
    { name: 'screens', testMatch: 'screens.spec.ts', use: chromium, dependencies: ['smoke'] },
  ],
})
