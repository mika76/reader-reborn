import { test, expect } from '@playwright/test'

// Subscriptions point at a closed port, so nothing is fetched from the internet; the posts come from a
// starred.json import, the way Google Reader's Takeout ones do.
const opml = `<?xml version="1.0"?>
<opml version="1.0"><head><title>Smoke</title></head><body>
  <outline text="Smoke Folder"><outline text="Smoke Feed" xmlUrl="http://127.0.0.1:9/smoke.xml" htmlUrl="http://127.0.0.1:9/"/></outline>
  <outline text="Loose Feed" xmlUrl="http://127.0.0.1:9/loose.xml"/>
</body></opml>`

const starred = {
  items: [1, 2, 3].map(n => ({
    id: `smoke-${n}`,
    title: `Starred post ${n}`,
    published: 1700000000 + n * 3600,
    alternate: [{ href: `https://example.com/${n}` }],
    content: { content: `<p>Body of post <b>${n}</b></p><script>window.hacked = true</script>` },
    origin: { streamId: 'feed/https://example.com/feed', title: 'Old Feed', htmlUrl: 'https://example.com/' },
  })),
}

test.beforeAll(async ({ request }) => {
  expect((await request.post('/api/opml', { data: opml })).ok()).toBe(true)
  expect((await request.post('/api/starred', { data: JSON.stringify(starred) })).ok()).toBe(true)
})

test('reading, starring and switching looks', async ({ page }) => {
  const errors = []
  page.on('pageerror', e => errors.push(e.message))
  page.on('console', m => m.type() === 'error' && errors.push(m.text()))

  await page.goto('/')
  const sidebar = page.locator('.sidebar')
  await expect(sidebar.getByText('All items')).toBeVisible()
  await expect(sidebar.getByText('Smoke Folder')).toBeVisible()
  await expect(sidebar.getByText('Smoke Feed')).toBeVisible()
  await expect(sidebar.getByText('Loose Feed')).toBeVisible()

  await sidebar.getByText('Starred items').click()
  await expect(page).toHaveURL(/#stream\/starred$/)
  const entries = page.locator('.entry')
  await expect(entries).toHaveCount(3)
  await expect(entries.first()).toContainText('Starred post 3')
  await expect(entries.first()).toContainText('Body of post 3')
  expect(await page.evaluate(() => window.hacked)).toBeUndefined()

  // j moves to the first post, s unstars it
  await page.keyboard.press('j')
  await expect(entries.first()).toHaveClass(/current/)
  await page.keyboard.press('s')
  await expect.poll(async () => (await (await page.request.get('/api/subscriptions')).json()).starred).toBe(2)

  // list view, then the keyboard help
  await page.keyboard.press('2')
  await expect(page.locator('.entries')).toHaveClass(/view-list/)
  await page.keyboard.press('?')
  await expect(page.locator('.help')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.locator('.help')).toHaveCount(0)

  // the Classic look, from Settings
  await page.goto('/#settings/preferences')
  await page.getByLabel(/Classic/).check()
  await expect(page.locator('.app')).toHaveClass(/theme-classic/)

  expect(errors).toEqual([])
})
