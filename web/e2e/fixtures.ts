import AxeBuilder from '@axe-core/playwright'
import { test as base, expect, type Page } from '@playwright/test'

export { expect }

export const owner = {
  email: process.env.E2E_OWNER_EMAIL ?? '',
  password: process.env.E2E_OWNER_PASSWORD ?? '',
}

/**
 * Every test fails when the page reports a Content Security Policy violation (docs/security.md section 7): a style or script the
 * proxy's policy blocks is missing from what the user sees, so it is a defect, not noise. Both the console message Chromium prints
 * and the `securitypolicyviolation` DOM event are collected, the event because it also names the blocked directive.
 */
export const test = base.extend<{ cspViolations: string[] }>({
  cspViolations: [
    async ({ page }, use) => {
      const violations: string[] = []
      page.on('console', (message) => {
        if (message.type() === 'error' && /Content Security Policy/i.test(message.text()))
          violations.push(message.text())
      })
      await page.exposeFunction('__reportCspViolation', (text: string) => violations.push(text))
      await page.addInitScript(() => {
        document.addEventListener('securitypolicyviolation', (event) => {
          const report = (window as unknown as { __reportCspViolation(text: string): void })
            .__reportCspViolation
          void report(`${event.violatedDirective} blocked ${event.blockedURI || 'inline'}`)
        })
      })
      await use(violations)
      // One line per distinct violation, cut short: a blocked stylesheet repeats on every render.
      const distinct = [...new Set(violations.map((text) => text.slice(0, 160)))]
      expect(distinct, 'Content Security Policy violations').toEqual([])
    },
    { auto: true },
  ],
})

export async function signIn(page: Page) {
  expect(owner.email, 'E2E_OWNER_EMAIL is set (run through scripts/e2e.sh)').not.toBe('')
  await page.goto('/sign-in')
  await page.getByLabel('Email').fill(owner.email)
  await page.getByLabel('Password').fill(owner.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible()
}

/**
 * Waits until the screen's queries have answered: no loading skeleton is left. Not `networkidle`: screens with a running
 * Evaluation Run poll every 2 s, so the network never goes quiet for long.
 */
export async function settled(page: Page) {
  await expect(page.locator('[aria-busy="true"]')).toHaveCount(0)
}

/**
 * The axe-core rules tagged WCAG 2.0/2.1 level A and AA (PRD section 9: accessible forms, labels and keyboard navigation). Serious
 * and critical violations fail the test; every violation is attached to the report for review. Nothing is excluded.
 */
export async function expectAccessible(page: Page, screen: string) {
  // A dialog fading in has text at partial opacity, which axe would measure as low contrast.
  await page.waitForFunction(() =>
    document.getAnimations().every((animation) => animation.playState !== 'running'),
  )
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze()
  await test.info().attach(`axe ${screen}`, {
    body: JSON.stringify(results.violations, null, 2),
    contentType: 'application/json',
  })
  // E2E_AXE_LOG=1 prints every screen's result, including the minor and moderate violations and the rules axe could not decide.
  if (process.env.E2E_AXE_LOG) {
    const incomplete = results.incomplete.map((rule) => rule.id).join(', ') || 'none'
    console.log(`axe ${screen}: ${results.passes.length} rules pass, incomplete: ${incomplete}`)
    for (const violation of results.violations)
      console.log(`axe ${screen}: ${violation.impact} ${violation.id} × ${violation.nodes.length}`)
  }
  const blocking = results.violations
    .filter((violation) => violation.impact === 'serious' || violation.impact === 'critical')
    .map((violation) => ({
      rule: violation.id,
      impact: violation.impact,
      help: violation.help,
      targets: violation.nodes.map((node) => node.target.join(' ')),
    }))
  expect(blocking, `serious or critical axe violations on ${screen}`).toEqual([])
}
