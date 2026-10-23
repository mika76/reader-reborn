import { reactive, computed, nextTick } from 'vue'
import * as api from './api.js'

const load = (key, fallback) => {
  try {
    const v = localStorage.getItem('reader.' + key)
    return v === null ? fallback : JSON.parse(v)
  } catch {
    return fallback
  }
}
export const persist = (key, value) => {
  try { localStorage.setItem('reader.' + key, JSON.stringify(value)) } catch { /* private mode */ }
}

export const state = reactive({
  folders: [],
  feeds: [],
  starred: 0,

  page: 'reader',          // 'reader' | 'settings'
  settingsTab: 'subscriptions',
  stream: 'all',           // 'all' | 'starred' | 'feed:<id>' | 'folder:<id>'
  query: '',               // search terms; searches within the stream

  items: [],
  continuation: null,
  maxId: 0,
  loading: false,
  loaded: false,
  currentId: null,
  openId: null,            // list view: the entry shown expanded

  theme: load('theme', 'modern'),         // 'modern' (2011-2013) | 'classic' (2008-2011)
  view: load('view', 'expanded'),         // 'expanded' | 'list'
  unreadOnly: load('unreadOnly', false),
  sort: 'newest',
  collapsed: load('collapsed', []),       // collapsed folder ids
  sidebarHidden: false,
  help: false,
  subscribeOpen: false,
  message: null,
})

// ---------------------------------------------------------------------------------------------
// Derived data

export const feedById = computed(() => new Map(state.feeds.map(f => [f.id, f])))
export const folderById = computed(() => new Map(state.folders.map(f => [f.id, f])))
export const unreadTotal = computed(() => state.feeds.reduce((n, f) => n + f.unread, 0))
export const looseFeeds = computed(() => state.feeds.filter(f => f.folderId == null))
export const feedsIn = folderId => state.feeds.filter(f => f.folderId === folderId)
export const folderUnread = folderId => feedsIn(folderId).reduce((n, f) => n + f.unread, 0)
export const formatCount = n => (n > 1000 ? '1000+' : String(n))

// Feeds in the order they appear in the sidebar, for shift+j / shift+k
export const sidebarStreams = computed(() => {
  const out = ['all', 'starred']
  for (const folder of state.folders) {
    out.push('folder:' + folder.id)
    if (!state.collapsed.includes(folder.id)) out.push(...feedsIn(folder.id).map(f => 'feed:' + f.id))
  }
  out.push(...looseFeeds.value.map(f => 'feed:' + f.id))
  return out
})

export const streamFeed = computed(() =>
  state.stream.startsWith('feed:') ? feedById.value.get(Number(state.stream.slice(5))) : null)
export const streamFolder = computed(() =>
  state.stream.startsWith('folder:') ? folderById.value.get(Number(state.stream.slice(7))) : null)

export const streamTitle = computed(() => {
  if (state.stream === 'starred') return 'Starred items'
  if (streamFeed.value) return streamFeed.value.title
  if (streamFolder.value) return streamFolder.value.name
  return 'All items'
})

export const streamUnread = computed(() => {
  if (streamFeed.value) return streamFeed.value.unread
  if (streamFolder.value) return folderUnread(streamFolder.value.id)
  if (state.stream === 'starred') return null
  return unreadTotal.value
})

export const currentItem = computed(() => state.items.find(i => i.id === state.currentId) ?? null)

// ---------------------------------------------------------------------------------------------
// Messages (the yellow bar at the top)

let messageTimer
export function flash(text, { sticky = false, error = false } = {}) {
  clearTimeout(messageTimer)
  state.message = text ? { text, error } : null
  if (text && !sticky) messageTimer = setTimeout(() => (state.message = null), 4000)
}

async function attempt(fn) {
  try {
    return await fn()
  } catch (e) {
    flash(e.message, { error: true })
    return undefined
  }
}

// ---------------------------------------------------------------------------------------------
// Routing: #stream/<id>, #search/<stream>/<terms>, #settings/<tab>

export function route() {
  const [kind, ...rest] = decodeURIComponent(location.hash.slice(1)).split('/')
  if (kind === 'settings') {
    state.page = 'settings'
    state.settingsTab = rest[0] || 'subscriptions'
    return
  }
  const stream = kind === 'stream' || kind === 'search' ? rest[0] || 'all' : 'all'
  const query = kind === 'search' ? rest.slice(1).join('/') : ''
  if (state.page === 'reader' && stream === state.stream && query === state.query && state.loaded) return
  state.page = 'reader'
  openStream(stream, query, false)
}

function setHash(hash) {
  if (location.hash !== hash) history.pushState(null, '', hash)
}

export function openSettings(tab = 'subscriptions') {
  state.page = 'settings'
  state.settingsTab = tab
  setHash('#settings/' + tab)
}

// ---------------------------------------------------------------------------------------------
// Loading

export async function loadSubscriptions() {
  const d = await attempt(() => api.get('/api/subscriptions'))
  if (!d) return
  state.folders = d.folders
  state.feeds = d.feeds
  state.starred = d.starred
}

let loadToken = 0

export function openStream(stream, query = '', updateHash = true) {
  state.page = 'reader'
  state.stream = stream
  state.query = query
  if (updateHash) setHash(query ? `#search/${stream}/${encodeURIComponent(query)}` : `#stream/${stream}`)
  return reload()
}

export function reload() {
  state.items = []
  state.continuation = null
  state.currentId = null
  state.openId = null
  state.loaded = false
  state.loading = false
  document.querySelector('.viewer')?.scrollTo(0, 0)
  return loadMore(true)
}

export async function loadMore(reset = false) {
  if (!reset && (state.loading || !state.continuation)) return
  const token = ++loadToken
  const params = new URLSearchParams({ stream: state.stream, sort: state.sort, limit: '40' })
  if (state.unreadOnly && state.stream !== 'starred') params.set('unread', 'true')
  if (state.query) params.set('q', state.query)
  if (!reset) {
    params.set('cursorTs', state.continuation.ts)
    params.set('cursorId', state.continuation.id)
  }
  state.loading = true
  const slow = setTimeout(() => token === loadToken && flash('Loading...', { sticky: true }), 400)
  try {
    const d = await attempt(() => api.get('/api/items?' + params))
    if (token !== loadToken || !d) return
    state.items.push(...d.items.map(i => ({ ...i, keepUnread: false })))
    state.continuation = d.continuation
    if (reset) state.maxId = d.maxId
  } finally {
    clearTimeout(slow)
    if (token === loadToken) {
      state.loading = false
      state.loaded = true
      if (state.message?.text === 'Loading...') flash(null)
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Item state

export function setRead(item, read) {
  if (item.isRead === read) return
  if (read && item.keepUnread) return
  item.isRead = read
  const feed = feedById.value.get(item.feedId)
  if (feed) feed.unread = Math.max(0, feed.unread + (read ? -1 : 1))
  attempt(() => api.post(`/api/items/${item.id}`, { read }))
}

export function toggleRead(item) {
  if (item.isRead) {
    setRead(item, false)
    item.keepUnread = true
  } else {
    item.keepUnread = false
    setRead(item, true)
  }
}

export function setKeepUnread(item, keep) {
  item.keepUnread = keep
  if (keep) setRead(item, false)
}

export function toggleStar(item) {
  item.isStarred = !item.isStarred
  state.starred += item.isStarred ? 1 : -1
  attempt(() => api.post(`/api/items/${item.id}`, { starred: item.isStarred }))
}

export async function markAllRead(olderThanDays) {
  if (state.stream === 'all' && !olderThanDays && unreadTotal.value > 20 &&
      !confirm(`Are you sure you want to mark ${formatCount(unreadTotal.value)} items as read?`)) return
  const r = await attempt(() => api.post('/api/items/mark-read', { stream: state.stream, maxId: state.maxId, olderThanDays }))
  if (!r) return
  const cutoff = olderThanDays ? Date.now() / 1000 - olderThanDays * 86400 : Infinity
  for (const item of state.items) if (item.id <= state.maxId && item.published < cutoff) item.isRead = true
  await loadSubscriptions()
  flash(`${streamTitle.value} has been marked as read.`)
}

// ---------------------------------------------------------------------------------------------
// Selection and navigation

export function select(item, { open = state.view === 'list', scroll = true, toggle = false } = {}) {
  if (!item) return
  state.currentId = item.id
  if (state.view === 'list') {
    state.openId = toggle && state.openId === item.id ? null : open ? item.id : state.openId
    if (state.openId === item.id) setRead(item, true)
  } else {
    setRead(item, true)
  }
  if (scroll) nextTick(() => scrollToItem(item.id))
  const index = state.items.indexOf(item)
  if (index > state.items.length - 6) loadMore()
}

export function move(delta, { open } = {}) {
  const index = state.items.findIndex(i => i.id === state.currentId)
  const next = state.items[index === -1 ? (delta > 0 ? 0 : -1) : index + delta]
  if (next) select(next, open === undefined ? {} : { open })
  else if (delta > 0) loadMore()
}

export function scrollToItem(id) {
  const viewer = document.querySelector('.viewer')
  const el = document.getElementById('entry-' + id)
  if (!viewer || !el) return
  if (state.view === 'expanded' || state.openId === id) {
    viewer.scrollTop = el.offsetTop
  } else {
    // list view: keep the row visible, like Reader did, without jumping
    const v = viewer.getBoundingClientRect()
    const r = el.getBoundingClientRect()
    if (r.top < v.top) viewer.scrollTop -= v.top - r.top
    else if (r.bottom > v.bottom) viewer.scrollTop += r.bottom - v.bottom
  }
}

export function moveSubscription(delta) {
  const list = sidebarStreams.value
  const index = list.indexOf(state.stream)
  const next = list[Math.min(list.length - 1, Math.max(0, index + delta))]
  if (next && next !== state.stream) openStream(next)
}

export function setView(view) {
  state.view = view
  persist('view', view)
  state.openId = view === 'list' ? null : state.openId
  if (state.currentId) nextTick(() => scrollToItem(state.currentId))
}

export function setTheme(theme) {
  state.theme = theme
  persist('theme', theme)
}

export function setUnreadOnly(on) {
  state.unreadOnly = on
  persist('unreadOnly', on)
  reload()
}

export function setSort(sort) {
  state.sort = sort
  reload()
}

export function toggleFolder(id) {
  const i = state.collapsed.indexOf(id)
  if (i === -1) state.collapsed.push(id)
  else state.collapsed.splice(i, 1)
  persist('collapsed', state.collapsed)
}

// ---------------------------------------------------------------------------------------------
// Subscriptions and folders

export async function refresh() {
  flash('Refreshing...', { sticky: true })
  const feed = streamFeed.value
  const r = await attempt(() => api.post('/api/refresh' + (feed ? `?feed=${feed.id}` : '')))
  await loadSubscriptions()
  if (!r) return
  await reload()
  flash(r.error ? `Couldn't refresh: ${r.error}` : r.newItems ? `${r.newItems} new items.` : 'No new items.', { error: !!r.error })
}

export async function subscribe(url, folderId = null) {
  flash('Subscribing...', { sticky: true })
  const feed = await attempt(() => api.post('/api/feeds', { url, folderId }))
  if (!feed) return false
  await loadSubscriptions()
  flash(`You have subscribed to "${feed.title}".`)
  openStream('feed:' + feed.id)
  return true
}

export async function unsubscribe(feed) {
  if (!confirm(`Are you sure you want to unsubscribe from "${feed.title}"?`)) return
  await attempt(() => api.del(`/api/feeds/${feed.id}`))
  if (state.stream === 'feed:' + feed.id) openStream('all')
  await loadSubscriptions()
  flash(`You have unsubscribed from "${feed.title}".`)
}

export async function renameFeed(feed) {
  const title = prompt('New name:', feed.title)
  if (title == null) return
  await attempt(() => api.patch(`/api/feeds/${feed.id}`, { title }))
  await loadSubscriptions()
}

export async function moveFeed(feed, folderId) {
  if (feed.folderId === folderId) return
  feed.folderId = folderId
  await attempt(() => api.patch(`/api/feeds/${feed.id}`, { folderId, setFolder: true }))
  await loadSubscriptions()
}

export async function newFolder(feed) {
  const name = prompt('New folder name:')
  if (!name?.trim()) return
  const folder = await attempt(() => api.post('/api/folders', { name }))
  if (!folder) return
  if (feed) await moveFeed(feed, folder.id)
  else await loadSubscriptions()
}

export async function renameFolder(folder) {
  const name = prompt('New folder name:', folder.name)
  if (!name?.trim()) return
  await attempt(() => api.patch(`/api/folders/${folder.id}`, { name }))
  await loadSubscriptions()
}

export async function deleteFolder(folder) {
  if (!confirm(`Delete the folder "${folder.name}"? Its subscriptions will be kept.`)) return
  await attempt(() => api.del(`/api/folders/${folder.id}`))
  if (state.stream === 'folder:' + folder.id) openStream('all')
  await loadSubscriptions()
}

export async function importOpml(text) {
  const r = await attempt(() => api.postRaw('/api/opml', text))
  if (!r) return
  flash(`Imported ${r.imported} subscriptions. They will be fetched in the background.`)
  await loadSubscriptions()
  setTimeout(loadSubscriptions, 15000)
}
