import { expect, expectAccessible, settled, signIn, test } from './fixtures'

/**
 * The smoke path of docs/testing.md section 1: sign in as the Owner, create a Tenant and an API Key in the dashboard, send an
 * inference request with that key, and find it in the request explorer with its Attempts. The Mock Provider and the alias
 * "balanced" come from scripts/seed-demo.sh, so no real Provider is called.
 */
test('the Owner creates a Tenant and an API Key, and the request it makes shows in the explorer', async ({
  page,
  request,
}) => {
  const tenantName = `e2e-${Date.now()}`
  let tenantId = ''
  let apiKey = ''
  let requestId = ''

  await test.step('sign in', async () => {
    await signIn(page)
    await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible()
  })

  await test.step('create a Tenant', async () => {
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: 'Tenants' })
      .click()
    await page.getByRole('button', { name: 'Create Tenant' }).click()
    const dialog = page.getByRole('dialog', { name: 'Create Tenant' })
    await dialog.getByLabel('Name').fill(tenantName)
    await expectAccessible(page, 'Create Tenant dialog')
    await dialog.getByRole('button', { name: 'Create Tenant' }).click()
    await expect(dialog).toBeHidden()
    await page.getByRole('link', { name: tenantName }).click()
    await expect(page.getByRole('heading', { level: 1, name: tenantName })).toBeVisible()
    tenantId = page.url().split('/tenants/')[1] ?? ''
    expect(tenantId).not.toBe('')
  })

  await test.step('allow the alias "balanced"', async () => {
    await page.getByLabel('Allowed Model Aliases').fill('balanced')
    await page.getByRole('button', { name: 'Save model access' }).click()
    await expect(page.getByText('balanced', { exact: true }).first()).toBeVisible()
  })

  await test.step('create an API Key and copy it from the one-time dialog', async () => {
    await page.getByRole('button', { name: 'Create key' }).click()
    const form = page.getByRole('dialog', { name: 'Create API Key' })
    await form.getByLabel('Name').fill('e2e smoke')
    await form.getByRole('button', { name: 'Create key' }).click()
    const shown = page.getByRole('dialog', { name: 'Copy the new key now' })
    await expect(shown).toBeVisible()
    await expectAccessible(page, 'new API Key dialog')
    apiKey = (await shown.locator('span[title]').first().getAttribute('title')) ?? ''
    expect(apiKey.length).toBeGreaterThan(20)
    await expect(shown.getByRole('button', { name: 'Done' })).toBeDisabled()
    await shown.getByLabel("I've saved the key somewhere safe").check()
    await shown.getByRole('button', { name: 'Done' }).click()
    await expect(shown).toBeHidden()
    await expect(page.getByRole('cell', { name: 'e2e smoke' })).toBeVisible()
  })

  await test.step('send an inference request with the key', async () => {
    const response = await request.post('/v1/chat/completions', {
      headers: { Authorization: `Bearer ${apiKey}` },
      data: {
        model: 'balanced',
        messages: [{ role: 'user', content: 'Hello from the smoke test' }],
      },
    })
    expect(response.status(), await response.text()).toBe(200)
    requestId = response.headers()['x-request-id'] ?? ''
    expect(requestId).not.toBe('')
    const body = await response.json()
    expect(body.choices[0].message.content).toContain('Hello from the smoke test')
  })

  await test.step('find the request in the explorer', async () => {
    await page.goto(`/requests?tenantId=${tenantId}`)
    // The request is recorded as it settles, so its row can take a moment to appear.
    await expect(async () => {
      await page.reload()
      await expect(page.locator(`a[title="${requestId}"]`)).toBeVisible({ timeout: 2_000 })
    }).toPass({ timeout: 30_000 })
    await settled(page)
    await expectAccessible(page, 'Requests')
  })

  await test.step('open it and see its Attempts', async () => {
    await page.locator(`a[title="${requestId}"]`).click()
    await expect(page).toHaveURL(new RegExp(`/requests/${requestId}$`))
    const attempts = page.getByRole('region', { name: 'Attempts' })
    await expect(attempts.getByRole('row')).toHaveCount(2)
    await expect(attempts.getByText('mock-echo')).toBeVisible()
    await expect(attempts.getByText('Succeeded')).toBeVisible()
    await settled(page)
    await expectAccessible(page, 'Request detail')
  })
})
