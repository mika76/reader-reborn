<script setup>
import { ref, computed } from 'vue'
import Entry from './Entry.vue'
import ViewerHeader from './ViewerHeader.vue'
import { state, loadMore, select, setUnreadOnly, move } from '../store.js'

const viewer = ref(null)
let userScrolled = false
let frame = 0

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
  <div class="viewer-frame">
    <ViewerHeader v-if="state.theme === 'classic'" />
    <div ref="viewer" class="viewer" @scroll="onScroll">
      <ViewerHeader v-if="state.theme !== 'classic'" />

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
    <div v-if="state.theme === 'classic'" class="viewer-footer">
      <button class="button" title="Previous item (k)" @click="move(-1)">&#9650; Previous item</button>
      <button class="button" title="Next item (j)" @click="move(1)">&#9660; Next item</button>
    </div>
  </div>
</template>
