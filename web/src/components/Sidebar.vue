<script setup>
import { ref, nextTick } from 'vue'
import Icon from './Icon.vue'
import FeedIcon from './FeedIcon.vue'
import {
  state, unreadTotal, looseFeeds, feedsIn, folderUnread, formatCount, feedById,
  openStream, openSettings, subscribe, toggleFolder, moveFeed,
} from '../store.js'

const url = ref('')
const input = ref(null)
const subsOpen = ref(true)
const dropTarget = ref(null)

async function toggleSubscribe() {
  state.subscribeOpen = !state.subscribeOpen
  if (state.subscribeOpen) {
    await nextTick()
    input.value?.focus()
  }
}

async function add() {
  if (!url.value.trim()) return
  if (await subscribe(url.value)) {
    url.value = ''
    state.subscribeOpen = false
  }
}

// Drag a subscription onto a folder (or onto "Subscriptions" to take it out of its folder)
function dragStart(e, feed) {
  e.dataTransfer.setData('text/x-reader-feed', String(feed.id))
  e.dataTransfer.effectAllowed = 'move'
}
function dragOver(e, target) {
  if (!e.dataTransfer.types.includes('text/x-reader-feed')) return
  e.preventDefault()
  dropTarget.value = target
}
function drop(e, folderId) {
  dropTarget.value = null
  const feed = feedById.value.get(Number(e.dataTransfer.getData('text/x-reader-feed')))
  if (feed) moveFeed(feed, folderId)
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
          <input ref="input" v-model="url" type="text" class="text-input" @keydown.esc="state.subscribeOpen = false">
          <button class="button" type="submit">Add</button>
        </form>
      </div>
    </div>

    <!-- only the lists scroll, so the subscribe box can overhang the main area -->
    <div class="sidebar-scroll">
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
      </ul>

      <div class="nav-section" :class="{ 'drop-target': dropTarget === 'root' }"
           @dragover="dragOver($event, 'root')" @dragleave="dropTarget = null" @drop="drop($event, null)">
        <span class="nav-toggle" @click="subsOpen = !subsOpen">
          <Icon :name="subsOpen ? 'triangle-open' : 'triangle-closed'" />
        </span>
        <span class="nav-section-title" @click="subsOpen = !subsOpen">Subscriptions</span>
      </div>

      <ul v-if="subsOpen" class="nav subscriptions">
        <template v-for="folder in state.folders" :key="'folder' + folder.id">
          <li class="nav-item folder"
              :class="{
                selected: state.stream === 'folder:' + folder.id && state.page === 'reader',
                unread: folderUnread(folder.id),
                'drop-target': dropTarget === folder.id,
              }"
              @click="openStream('folder:' + folder.id)"
              @dragover="dragOver($event, folder.id)" @dragleave="dropTarget = null" @drop="drop($event, folder.id)">
            <span class="nav-toggle" @click.stop="toggleFolder(folder.id)">
              <Icon :name="state.collapsed.includes(folder.id) ? 'triangle-closed' : 'triangle-open'" />
            </span>
            <Icon name="folder" class="favicon folder-icon" />
            <span class="nav-name">{{ folder.name }}</span>
            <span v-if="folderUnread(folder.id)" class="nav-count">({{ formatCount(folderUnread(folder.id)) }})</span>
          </li>
          <template v-if="!state.collapsed.includes(folder.id)">
            <li v-for="feed in feedsIn(folder.id)" :key="feed.id" class="nav-item feed in-folder"
                :class="{ selected: state.stream === 'feed:' + feed.id && state.page === 'reader', unread: feed.unread, error: feed.lastError }"
                :title="feed.lastError ? 'Error: ' + feed.lastError : feed.title"
                draggable="true" @dragstart="dragStart($event, feed)" @click="openStream('feed:' + feed.id)">
              <FeedIcon :feed="feed" />
              <span class="nav-name">{{ feed.title }}</span>
              <span v-if="feed.unread" class="nav-count">({{ formatCount(feed.unread) }})</span>
            </li>
          </template>
        </template>
        <li v-for="feed in looseFeeds" :key="feed.id" class="nav-item feed"
            :class="{ selected: state.stream === 'feed:' + feed.id && state.page === 'reader', unread: feed.unread, error: feed.lastError }"
            :title="feed.lastError ? 'Error: ' + feed.lastError : feed.title"
            draggable="true" @dragstart="dragStart($event, feed)" @click="openStream('feed:' + feed.id)">
          <FeedIcon :feed="feed" />
          <span class="nav-name">{{ feed.title }}</span>
          <span v-if="feed.unread" class="nav-count">({{ formatCount(feed.unread) }})</span>
        </li>
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
