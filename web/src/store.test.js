import { describe, it, expect, beforeEach, vi } from 'vitest'

vi.mock('./api.js', () => ({
  get: vi.fn(async () => ({ items: [], continuation: null, maxId: 0 })),
  post: vi.fn(async () => ({})),
  postRaw: vi.fn(async () => ({})),
  patch: vi.fn(async () => null),
  del: vi.fn(async () => null),
}))

const api = await import('./api.js')
const store = await import('./store.js')
const { state } = store

const feed = (id, title, unread, folderIds = [], extra = {}) =>
  ({ id, title, unread, folderIds, folderSort: {}, sort: 0, url: `https://x/${id}`, lastError: null, ...extra })

beforeEach(() => {
  vi.clearAllMocks()
  localStorage.clear()
  Object.assign(state, {
    folders: [{ id: 1, name: 'Tech', sort: 2 }, { id: 2, name: 'comics', sort: 0 }],
    feeds: [
      feed(10, 'Zebra News', 3, [1]),
      feed(11, 'apple blog', 5, [1, 2]),
      feed(12, 'Loose', 7, [], { sort: 1 }),
      feed(13, 'Another loose', 0, [], { sort: 3 }),
    ],
    starred: 4,
    stream: 'all',
    query: '',
    page: 'reader',
    order: 'alpha',
    collapsed: [],
    items: [],
    maxId: 0,
    unreadOnly: false,
  })
})

describe('the sidebar tree', () => {
  it('puts folders first, then feeds, each alphabetically ignoring case', () => {
    expect(store.topLevel.value.map(i => i.name)).toEqual(['comics', 'Tech', 'Another loose', 'Loose'])
  })

  it('uses the drag and drop order when chosen', () => {
    state.order = 'manual'
    expect(store.topLevel.value.map(i => i.name)).toEqual(['comics', 'Loose', 'Tech', 'Another loose'])
  })

  it('orders feeds inside a folder by their place there', () => {
    state.order = 'manual'
    state.feeds[0].folderSort = { 1: 0 }
    state.feeds[1].folderSort = { 1: 1 }
    expect(store.folderChildren(1).map(f => f.title)).toEqual(['Zebra News', 'apple blog'])
  })

  it('counts unread items per folder and in total', () => {
    expect(store.folderUnread(1)).toBe(8)
    expect(store.folderUnread(2)).toBe(5)
    expect(store.unreadTotal.value).toBe(15)
  })

  it('lists the streams in sidebar order for shift+j and shift+k, skipping collapsed folders', () => {
    expect(store.sidebarStreams.value).toEqual(
      ['all', 'starred', 'folder:2', 'feed:11', 'folder:1', 'feed:10', 'feed:11', 'feed:13', 'feed:12'])
    store.toggleFolder(1)
    expect(store.sidebarStreams.value).toEqual(['all', 'starred', 'folder:2', 'feed:11', 'folder:1', 'feed:13', 'feed:12'])
    expect(JSON.parse(localStorage.getItem('reader.collapsed'))).toEqual([1])
  })

  it('shift+c collapses every folder, then expands them all', () => {
    store.toggleAllFolders()
    expect(state.collapsed).toEqual([1, 2])
    store.toggleAllFolders()
    expect(state.collapsed).toEqual([])
  })
})

describe('the current stream', () => {
  it('has a title and unread count for each kind', () => {
    expect([store.streamTitle.value, store.streamUnread.value]).toEqual(['All items', 15])
    state.stream = 'feed:10'
    expect([store.streamTitle.value, store.streamUnread.value]).toEqual(['Zebra News', 3])
    state.stream = 'folder:1'
    expect([store.streamTitle.value, store.streamUnread.value]).toEqual(['Tech', 8])
    state.stream = 'starred'
    expect([store.streamTitle.value, store.streamUnread.value]).toEqual(['Starred items', null])
  })
})

describe('formatCount', () => {
  it('stops at 1000+, as Reader did', () => {
    expect(store.formatCount(7)).toBe('7')
    expect(store.formatCount(1000)).toBe('1000')
    expect(store.formatCount(1001)).toBe('1000+')
  })
})

describe('reading', () => {
  const item = (id, feedId, extra = {}) => ({ id, feedId, isRead: false, isStarred: false, keepUnread: false, published: 0, ...extra })

  it('marking read lowers the feed count and tells the server', () => {
    const i = item(1, 10)
    store.setRead(i, true)
    expect(i.isRead).toBe(true)
    expect(store.feedById.value.get(10).unread).toBe(2)
    expect(api.post).toHaveBeenCalledWith('/api/items/1', { read: true })
    store.setRead(i, true)
    expect(api.post).toHaveBeenCalledTimes(1)
  })

  it('keep unread stops a post being marked read as you pass it', () => {
    const i = item(1, 10)
    store.setKeepUnread(i, true)
    store.setRead(i, true)
    expect(i.isRead).toBe(false)
    expect(api.post).not.toHaveBeenCalled()
  })

  it('m on a read post marks it unread and keeps it so', () => {
    const i = item(1, 10, { isRead: true })
    store.toggleRead(i)
    expect(i.isRead).toBe(false)
    expect(i.keepUnread).toBe(true)
    expect(store.feedById.value.get(10).unread).toBe(4)
    store.toggleRead(i)
    expect(i.isRead).toBe(true)
    expect(i.keepUnread).toBe(false)
  })

  it('starring changes the starred count', () => {
    const i = item(1, 10)
    store.toggleStar(i)
    expect(i.isStarred).toBe(true)
    expect(state.starred).toBe(5)
    expect(api.post).toHaveBeenCalledWith('/api/items/1', { starred: true })
  })

  it('mark all as read asks first when there are many, and marks what was loaded', async () => {
    state.feeds[2].unread = 70
    window.confirm = vi.fn(() => false)
    await store.markAllRead()
    expect(window.confirm).toHaveBeenCalled()
    expect(api.post).not.toHaveBeenCalled()

    window.confirm = vi.fn(() => true)
    state.maxId = 2
    state.items = [item(1, 10), item(2, 10), item(3, 10)]
    api.get.mockResolvedValueOnce({ folders: state.folders, feeds: state.feeds, starred: 4 })
    await store.markAllRead()
    expect(api.post).toHaveBeenCalledWith('/api/items/mark-read', { stream: 'all', maxId: 2, olderThanDays: undefined })
    expect(state.items.map(i => i.isRead)).toEqual([true, true, false])
  })

  it('mark older than a day only marks older posts', async () => {
    state.stream = 'feed:10'
    state.maxId = 9
    const now = Date.now() / 1000
    state.items = [item(1, 10, { published: now - 3 * 86400 }), item(2, 10, { published: now - 3600 })]
    api.get.mockResolvedValueOnce({ folders: state.folders, feeds: state.feeds, starred: 4 })
    await store.markAllRead(1)
    expect(state.items.map(i => i.isRead)).toEqual([true, false])
  })
})

describe('routing', () => {
  it('opens settings and trends tabs', () => {
    location.hash = '#settings/import'
    store.route()
    expect([state.page, state.settingsTab]).toEqual(['settings', 'import'])
    location.hash = '#trends/inactive'
    store.route()
    expect([state.page, state.trendsTab]).toEqual(['trends', 'inactive'])
  })

  it('opens a search within a stream', async () => {
    location.hash = '#search/feed:10/' + encodeURIComponent('cats & dogs')
    store.route()
    expect([state.page, state.stream, state.query]).toEqual(['reader', 'feed:10', 'cats & dogs'])
    const url = api.get.mock.calls.at(-1)[0]
    const params = new URLSearchParams(url.split('?')[1])
    expect(params.get('stream')).toBe('feed:10')
    expect(params.get('q')).toBe('cats & dogs')
  })

  it('asks only for unread items when New items is chosen', () => {
    state.unreadOnly = true
    store.openStream('feed:11')
    expect(api.get.mock.calls.at(-1)[0]).toContain('unread=true')
    store.openStream('starred')
    expect(api.get.mock.calls.at(-1)[0]).not.toContain('unread=true')
  })

  it('fills in the subscribe box from the bookmarklet', () => {
    location.hash = '#subscribe/' + encodeURIComponent('https://example.com/blog')
    store.route()
    expect(state.subscribeOpen).toBe(true)
    expect(state.subscribeUrl).toBe('https://example.com/blog')
    expect(location.hash).toBe('#stream/all')
  })
})

describe('drag and drop', () => {
  it('dropping a feed after another saves the new order of that level', async () => {
    await store.reorder({ type: 'feed', id: 12, from: null }, null, { type: 'folder', id: 2 }, 'after')
    expect(state.order).toBe('manual')
    // manual order before the drop: comics(0), Loose(1), Tech(2), Another loose(3)
    expect(api.post).toHaveBeenCalledWith('/api/order', {
      folderId: null,
      items: [{ type: 'folder', id: 2 }, { type: 'feed', id: 12 }, { type: 'folder', id: 1 }, { type: 'feed', id: 13 }],
    })
  })

  it('dropping a feed from a folder into the top level moves it out of the folder first', async () => {
    await store.reorder({ type: 'feed', id: 10, from: 1 }, null, { type: 'feed', id: 13 }, 'before')
    expect(api.patch).toHaveBeenCalledWith('/api/feeds/10', { removeFolder: 1, addFolder: null })
  })

  it('folders can only be arranged at the top level', async () => {
    await store.reorder({ type: 'folder', id: 1, from: null }, 2, { type: 'feed', id: 11 }, 'before')
    expect(api.post).not.toHaveBeenCalled()
  })
})

describe('pausing a feed', () => {
  it('leaves its unread posts out of the folder and total counts', () => {
    state.feeds[1].pausedUntil = Date.now() / 1000 + 86400
    expect(store.isPaused(state.feeds[1])).toBe(true)
    expect(store.folderUnread(1)).toBe(3)
    expect(store.folderUnread(2)).toBe(0)
    expect(store.unreadTotal.value).toBe(10)
  })

  it('asks the server for so many days, or 0 to resume', async () => {
    const { folders, feeds } = state
    api.get.mockImplementation(async url =>
      url === '/api/subscriptions' ? { folders, feeds, starred: 4 } : { items: [], continuation: null, maxId: 0 })
    await store.pauseFeed(state.feeds[0], 7)
    expect(api.patch).toHaveBeenCalledWith('/api/feeds/10', { pauseDays: 7 })
    // All items is open, so the list reloads without the paused feed's posts
    expect(api.get.mock.calls.at(-1)[0]).toMatch(/^\/api\/items\?stream=all/)
    await store.resumeFeed(state.feeds[0])
    expect(api.patch).toHaveBeenCalledWith('/api/feeds/10', { pauseDays: 0 })
    api.get.mockReset()
    api.get.mockImplementation(async () => ({ items: [], continuation: null, maxId: 0 }))
  })
})
