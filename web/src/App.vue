<script setup>
import { ref, watchEffect, onMounted, onBeforeUnmount, nextTick } from 'vue'
import Icon from './components/Icon.vue'
import Sidebar from './components/Sidebar.vue'
import Toolbar from './components/Toolbar.vue'
import Viewer from './components/Viewer.vue'
import Settings from './components/Settings.vue'
import Trends from './components/Trends.vue'
import Help from './components/Help.vue'
import ServerStatus from './components/ServerStatus.vue'
import {
  state, unreadTotal, currentItem, formatCount, route, loadSubscriptions, openStream, openSettings,
  move, select, toggleStar, toggleAllFolders, toggleRead, markAllRead, refresh, setView, setTheme, moveSubscription,
} from './store.js'

const search = ref('')
const searchInput = ref(null)

watchEffect(() => {
  document.title = unreadTotal.value ? `Reader (${formatCount(unreadTotal.value)})` : 'Reader'
})

function doSearch() {
  openStream(state.stream === 'starred' || state.stream.startsWith('f') ? state.stream : 'all', search.value.trim())
  searchInput.value?.blur()
}

// ---------------------------------------------------------------------------------------------
// Keyboard shortcuts, the same keys Google Reader used

let pendingG = false
let pendingTimer

function onKey(e) {
  const t = e.target
  if (e.metaKey || e.ctrlKey || e.altKey) return
  if (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName)) return
  if (state.statusOpen) {
    if (e.key === 'Escape') state.statusOpen = false
    return
  }
  if (state.help && e.key !== '?') {
    if (e.key === 'Escape') state.help = false
    return
  }

  const key = e.key
  if (pendingG) {
    pendingG = false
    const go = { a: () => openStream('all'), s: () => openStream('starred'), t: () => openSettings() }[key.toLowerCase()]
    if (go) { e.preventDefault(); go() }
    return
  }
  if (state.page !== 'reader' && !['?', 'g', '/', 'u', 'C'].includes(key)) return

  const item = currentItem.value
  const viewer = document.querySelector('.viewer')
  const actions = {
    j: () => move(1),
    k: () => move(-1),
    n: () => move(1, { open: false }),
    p: () => move(-1, { open: false }),
    J: () => moveSubscription(1),
    C: () => toggleAllFolders(),
    K: () => moveSubscription(-1),
    o: () => item && select(item, { open: true, toggle: true }),
    Enter: () => item && select(item, { open: true, toggle: true }),
    s: () => item && toggleStar(item),
    m: () => item && toggleRead(item),
    v: () => item?.url && window.open(item.url, '_blank', 'noopener'),
    A: () => markAllRead(),
    r: () => refresh(),
    u: () => (state.sidebarHidden = !state.sidebarHidden),
    1: () => setView('expanded'),
    2: () => setView('list'),
    a: () => { state.sidebarHidden = false; state.subscribeOpen = true; nextTick(() => document.querySelector('.subscribe-box input')?.focus()) },
    '/': () => searchInput.value?.focus(),
    '?': () => (state.help = !state.help),
    ' ': () => viewer?.scrollBy({ top: (e.shiftKey ? -1 : 1) * viewer.clientHeight * 0.8 }),
    Escape: () => { state.subscribeOpen = false },
    g: () => {
      pendingG = true
      clearTimeout(pendingTimer)
      pendingTimer = setTimeout(() => (pendingG = false), 1500)
    },
  }
  const action = actions[key]
  if (action) {
    e.preventDefault()
    action()
  }
}

let poll
onMounted(async () => {
  window.addEventListener('keydown', onKey)
  window.addEventListener('popstate', route)
  await loadSubscriptions()
  route()
  // pick up items the background downloader has fetched
  poll = setInterval(loadSubscriptions, 120000)
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKey)
  window.removeEventListener('popstate', route)
  clearInterval(poll)
})
</script>

<template>
  <div class="app" :class="['theme-' + state.theme, { 'sidebar-hidden': state.sidebarHidden }]">
    <header class="top">
      <a class="logo" href="#stream/all" title="Reader home">
        <Icon name="feed" class="logo-mark" /><span>Reader</span>
      </a>
      <form class="search" @submit.prevent="doSearch">
        <input ref="searchInput" v-model="search" class="search-input" type="text" aria-label="Search"
               @keydown.esc="searchInput.blur()">
        <button v-if="state.theme === 'classic'" class="button search-button" type="submit">Search</button>
        <button v-else class="button button-blue search-button" type="submit" title="Search"><Icon name="search" /></button>
      </form>
      <div v-if="state.theme === 'classic'" class="top-links">
        <a @click="openSettings()">Settings</a> |
        <a @click="state.help = true">Keyboard shortcuts</a> |
        <a @click="state.statusOpen = true">Server status</a> |
        <a @click="setTheme('modern')">Modern look</a>
      </div>
    </header>

    <div v-if="state.theme !== 'classic'" class="bar">
      <div class="brand" @click="openStream('all')">Reader</div>
      <Toolbar />
    </div>

    <div class="body">
      <Sidebar v-show="!state.sidebarHidden" />
      <main class="main">
        <Settings v-if="state.page === 'settings'" />
        <Trends v-else-if="state.page === 'trends'" />
        <Viewer v-else />
      </main>
    </div>

    <div v-if="state.message" class="message" :class="{ error: state.message.error }">{{ state.message.text }}</div>
    <Help v-if="state.help" />
    <ServerStatus v-if="state.statusOpen" />
  </div>
</template>
