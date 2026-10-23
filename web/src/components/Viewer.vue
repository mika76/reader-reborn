<script setup>
import { ref, computed } from 'vue'
import Entry from './Entry.vue'
import ViewerHeader from './ViewerHeader.vue'
import { state, loadMore, select, setRead, setUnreadOnly, move } from '../store.js'

const viewer = ref(null)
let userScrolled = false
let pending = 0

const emptyText = computed(() => {
  if (state.query) return `No results found for "${state.query}".`
  if (state.stream === 'starred') return 'You have no starred items. Use stars to save items you want to come back to.'
  if (state.unreadOnly) return 'You have no unread items.'
  return 'There are no items here yet.'
})

// Handled at most every 80 ms. A timer rather than requestAnimationFrame, which some browsers pause
// (embedded and background views), and which would then stop more items loading.
function onScroll() {
  if (!pending) pending = setTimeout(handleScroll, 80)
}

// Loads more near the end. In expanded view, every item whose top has reached the top of the viewer is
// read, like Reader did, even ones that went by in one big scroll; the one at the top becomes current.
function handleScroll() {
  pending = 0
  const el = viewer.value
  if (!el) return
  if (el.scrollTop + el.clientHeight > el.scrollHeight - 800) loadMore()
  if (state.view !== 'expanded') return
  if (el.scrollTop > 0) userScrolled = true
  if (!userScrolled) return
  const top = el.scrollTop + 8
  let reached = null
  for (const item of state.items) {
    const node = document.getElementById('entry-' + item.id)
    if (!node) continue
    if (node.offsetTop > top) break
    setRead(item, true) // leaves "Keep unread" items alone
    reached = item
  }
  if (reached && state.currentId !== reached.id) select(reached, { scroll: false })
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
