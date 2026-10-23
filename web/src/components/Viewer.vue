<script setup>
import { ref, computed } from 'vue'
import Entry from './Entry.vue'
import {
  state, streamTitle, streamFeed, streamUnread, formatCount, loadMore, select, setUnreadOnly, openStream,
} from '../store.js'

const viewer = ref(null)
let userScrolled = false
let frame = 0

const subtitle = computed(() => {
  if (state.stream === 'starred') return null
  const n = streamUnread.value
  return n ? `${formatCount(n)} new item${n === 1 ? '' : 's'}` : 'No new items'
})

const emptyText = computed(() => {
  if (state.query) return `No results found for "${state.query}".`
  if (state.stream === 'starred') return 'You have no starred items. Use stars to save items you want to come back to.'
  if (state.unreadOnly) return 'You have no unread items.'
  return 'There are no items here yet.'
})

// Expanded view marks items read as they reach the top of the viewer, like Reader did.
function onScroll() {
  cancelAnimationFrame(frame)
  frame = requestAnimationFrame(() => {
    const el = viewer.value
    if (!el) return
    if (el.scrollTop + el.clientHeight > el.scrollHeight - 800) loadMore()
    if (state.view !== 'expanded') return
    if (el.scrollTop > 0) userScrolled = true
    if (!userScrolled) return
    const top = el.scrollTop + 8
    for (const item of state.items) {
      const node = document.getElementById('entry-' + item.id)
      if (node && node.offsetTop <= top && node.offsetTop + node.offsetHeight > top) {
        if (state.currentId !== item.id) select(item, { scroll: false })
        break
      }
    }
  })
}

defineExpose({ resetScroll: () => (userScrolled = false) })
</script>

<template>
  <div ref="viewer" class="viewer" @scroll="onScroll">
    <div class="viewer-header">
      <h1 class="stream-title">
        <template v-if="state.query">Search results for "{{ state.query }}"</template>
        <template v-else-if="streamFeed?.siteUrl">
          <a :href="streamFeed.siteUrl" target="_blank" rel="noopener">{{ streamTitle }} &raquo;</a>
        </template>
        <template v-else>{{ streamTitle }}</template>
      </h1>
      <div class="stream-subtitle">
        <template v-if="state.query">
          in <a @click="openStream(state.stream)">{{ streamTitle }}</a>
        </template>
        <template v-else-if="subtitle">{{ subtitle }}</template>
        <span v-if="streamFeed?.lastError" class="stream-error">
          &nbsp;&middot; Error fetching this feed: {{ streamFeed.lastError }}
        </span>
      </div>
    </div>

    <div class="entries" :class="'view-' + state.view">
      <Entry v-for="item in state.items" :key="item.id" :item="item" />

      <div v-if="state.loaded && !state.items.length" class="entries-empty">
        <p>{{ emptyText }}</p>
        <p v-if="state.unreadOnly && !state.query && state.stream !== 'starred'">
          <a @click="setUnreadOnly(false)">Show all items</a>
        </p>
      </div>
      <div v-else-if="state.loaded && !state.continuation" class="entries-end"
           :style="{ height: state.view === 'expanded' ? 'calc(100vh - 160px)' : null }">
        No more items.
      </div>
      <div v-else-if="state.loading" class="entries-loading">Loading...</div>
    </div>
  </div>
</template>
