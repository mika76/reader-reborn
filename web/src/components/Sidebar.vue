<script setup>
import { ref, nextTick, watch } from 'vue'
import Icon from './Icon.vue'
import FeedIcon from './FeedIcon.vue'
import {
  state, unreadTotal, topLevel, folderChildren, folderUnread, formatCount, feedById,
  openStream, openSettings, openTrends, subscribe, toggleFolder, moveFeed, reorder,
} from '../store.js'

const input = ref(null)
const subsOpen = ref(true)
const scroller = ref(null)

// Whenever a feed or folder is opened (from Trends, Settings, a post's "from" link, shift+j/k...),
// show it in the tree: open the folder it's in if that's collapsed, and scroll it into view
watch(() => state.page === 'reader' && state.stream, async stream => {
  if (!stream || !/^(feed|folder):/.test(stream)) return
  subsOpen.value = true
  const feed = stream.startsWith('feed:') ? feedById.value.get(Number(stream.slice(5))) : null
  if (feed?.folderIds.length && feed.folderIds.every(id => state.collapsed.includes(id))) toggleFolder(feed.folderIds[0])
  await nextTick()
  const row = scroller.value?.querySelector('.nav-item.selected')
  if (!row) return
  const view = scroller.value.getBoundingClientRect()
  const r = row.getBoundingClientRect()
  // leave it alone if it's already in view, otherwise bring it to the middle
  if (r.top < view.top || r.bottom > view.bottom) row.scrollIntoView({ block: 'center' })
})
const dropAt = ref(null) // { key, pos } of the row a drop would land on
let dragging = null      // { type, id, from }: dataTransfer can't be read during dragover

async function toggleSubscribe() {
  state.subscribeOpen = !state.subscribeOpen
  if (state.subscribeOpen) {
    await nextTick()
    input.value?.focus()
  }
}

async function add() {
  if (!state.subscribeUrl.trim()) return
  if (await subscribe(state.subscribeUrl)) {
    state.subscribeUrl = ''
    state.subscribeOpen = false
  }
}

// Drag and drop in the sidebar, as in Reader: drop a feed onto a folder to move it there, onto
// "Subscriptions" to take it out of its folder, or between rows to put feeds and folders in your own order.
// A row is { type: 'feed' | 'folder', id, container } where container is the folder it's in (null at the top).
const rowKey = row => `${row.container}/${row.type}:${row.id}`
const dropClass = row => (dropAt.value?.key === rowKey(row) ? 'drop-' + dropAt.value.pos : null)

function dragStart(e, row) {
  dragging = { type: row.type, id: row.id, from: row.container }
  e.dataTransfer.setData('text/plain', rowKey(row))
  e.dataTransfer.effectAllowed = 'move'
}

function dragEnd() {
  dragging = null
  dropAt.value = null
}

// Where a drop on this row would land: 'before' or 'after' it, or 'into' a folder
function dropPosition(e, row) {
  if (!dragging || (dragging.type === row.type && dragging.id === row.id)) return null
  const r = e.currentTarget.getBoundingClientRect()
  const y = (e.clientY - r.top) / r.height
  if (row.type === 'folder' && dragging.type === 'feed') {
    if (y < 0.25) return 'before'
    // below an open folder's name is its first feed, so the lower edge means "into" too
    return y > 0.75 && state.collapsed.includes(row.id) ? 'after' : 'into'
  }
  return y < 0.5 ? 'before' : 'after'
}

// Folders can't go inside folders: over a feed in a folder, a dragged folder goes after that folder
const dropRow = (row, pos) =>
  dragging?.type === 'folder' && row.container !== null
    ? [{ type: 'folder', id: row.container, container: null }, 'after']
    : [row, pos]

function dragOver(e, row) {
  const pos = dropPosition(e, row)
  if (!pos) return
  e.preventDefault()
  dropAt.value = { key: rowKey(row), pos }
}

function drop(e, row) {
  const drag = dragging
  const [target, pos] = dropRow(row, dropPosition(e, row))
  dragEnd()
  if (!drag || !pos) return
  e.preventDefault()
  if (pos === 'into') moveFeed(feedById.value.get(drag.id), drag.from, target.id)
  else reorder(drag, target.container, target, pos)
}

function dragOverRoot(e) {
  if (dragging?.type !== 'feed' || dragging.from === null) return
  e.preventDefault()
  dropAt.value = { key: 'root', pos: 'into' }
}

function dropOnRoot(e) {
  const drag = dragging
  dragEnd()
  if (drag?.type === 'feed') {
    e.preventDefault()
    moveFeed(feedById.value.get(drag.id), drag.from, null)
  }
}
</script>

<template>
  <nav class="sidebar">
    <div class="subscribe-area">
      <button v-if="state.theme === 'classic'" class="button" @click="toggleSubscribe">+ Add a subscription</button>
      <button v-else class="button button-red" @click="toggleSubscribe">Subscribe</button>
      <div v-if="state.subscribeOpen" class="subscribe-box">
        <div class="subscribe-hint">Paste a link to a blog or feed:</div>
        <form @submit.prevent="add">
          <input ref="input" v-model="state.subscribeUrl" type="text" class="text-input" @keydown.esc="state.subscribeOpen = false">
          <button class="button" type="submit">Add</button>
        </form>
      </div>
    </div>

    <!-- only the lists scroll, so the subscribe box can overhang the main area -->
    <div ref="scroller" class="sidebar-scroll">
      <ul class="nav">
        <li class="nav-item" :class="{ selected: state.stream === 'all' && state.page === 'reader', unread: unreadTotal }"
            @click="openStream('all')">
          <span class="nav-name">All items</span>
          <span v-if="unreadTotal" class="nav-count">({{ formatCount(unreadTotal) }})</span>
        </li>
        <li class="nav-item" :class="{ selected: state.stream === 'starred' && state.page === 'reader' }"
            @click="openStream('starred')">
          <Icon name="star-on" class="nav-star" />
          <span class="nav-name">Starred items</span>
        </li>
        <li class="nav-item" :class="{ selected: state.page === 'trends' }" @click="openTrends()">
          <Icon name="trends" class="nav-star" />
          <span class="nav-name">Trends</span>
        </li>
      </ul>

      <div class="nav-section" :class="{ 'drop-into': dropAt?.key === 'root' }"
           @dragover="dragOverRoot" @dragleave="dropAt = null" @drop="dropOnRoot">
        <span class="nav-toggle" @click="subsOpen = !subsOpen">
          <Icon :name="subsOpen ? 'triangle-open' : 'triangle-closed'" />
        </span>
        <span class="nav-section-title" @click="subsOpen = !subsOpen">Subscriptions</span>
      </div>

      <ul v-if="subsOpen" class="nav subscriptions" @dragend="dragEnd">
        <template v-for="item in topLevel" :key="item.type + item.id">
          <template v-if="item.type === 'folder'">
            <li class="nav-item folder"
                :class="[dropClass({ ...item, container: null }), {
                  selected: state.stream === 'folder:' + item.id && state.page === 'reader',
                  unread: folderUnread(item.id),
                }]"
                draggable="true" @dragstart="dragStart($event, { ...item, container: null })"
                @dragover="dragOver($event, { ...item, container: null })" @dragleave="dropAt = null"
                @drop="drop($event, { ...item, container: null })"
                @click="openStream('folder:' + item.id)">
              <span class="nav-toggle" @click.stop="toggleFolder(item.id)">
                <Icon :name="state.collapsed.includes(item.id) ? 'triangle-closed' : 'triangle-open'" />
              </span>
              <Icon name="folder" class="favicon folder-icon" />
              <span class="nav-name">{{ item.name }}</span>
              <span v-if="folderUnread(item.id)" class="nav-count">({{ formatCount(folderUnread(item.id)) }})</span>
            </li>
            <template v-if="!state.collapsed.includes(item.id)">
              <li v-for="feed in folderChildren(item.id)" :key="feed.id" class="nav-item feed in-folder"
                  :class="[dropClass({ type: 'feed', id: feed.id, container: item.id }), {
                    selected: state.stream === 'feed:' + feed.id && state.page === 'reader', unread: feed.unread, error: feed.lastError,
                  }]"
                  :title="feed.lastError ? 'Error: ' + feed.lastError : feed.title"
                  draggable="true" @dragstart="dragStart($event, { type: 'feed', id: feed.id, container: item.id })"
                  @dragover="dragOver($event, { type: 'feed', id: feed.id, container: item.id })" @dragleave="dropAt = null"
                  @drop="drop($event, { type: 'feed', id: feed.id, container: item.id })"
                  @click="openStream('feed:' + feed.id)">
                <FeedIcon :feed="feed" />
                <span class="nav-name">{{ feed.title }}</span>
                <span v-if="feed.unread" class="nav-count">({{ formatCount(feed.unread) }})</span>
              </li>
            </template>
          </template>
          <li v-else class="nav-item feed"
              :class="[dropClass({ ...item, container: null }), {
                selected: state.stream === 'feed:' + item.id && state.page === 'reader', unread: item.feed.unread, error: item.feed.lastError,
              }]"
              :title="item.feed.lastError ? 'Error: ' + item.feed.lastError : item.name"
              draggable="true" @dragstart="dragStart($event, { ...item, container: null })"
              @dragover="dragOver($event, { ...item, container: null })" @dragleave="dropAt = null"
              @drop="drop($event, { ...item, container: null })"
              @click="openStream('feed:' + item.id)">
            <FeedIcon :feed="item.feed" />
            <span class="nav-name">{{ item.name }}</span>
            <span v-if="item.feed.unread" class="nav-count">({{ formatCount(item.feed.unread) }})</span>
          </li>
        </template>
        <li v-if="!state.feeds.length" class="nav-empty">
          You have no subscriptions yet. Click <b>Subscribe</b> to add one, or
          <a href="#settings/import">import</a> them.
        </li>
      </ul>
      <div v-if="subsOpen && state.theme === 'classic'" class="nav-manage">
        <a @click="openSettings('subscriptions')">Manage subscriptions &raquo;</a>
      </div>
    </div>
  </nav>
</template>
