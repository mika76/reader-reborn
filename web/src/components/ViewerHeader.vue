<script setup>
import { computed } from 'vue'
import Toolbar from './Toolbar.vue'
import { state, streamTitle, streamFeed, streamUnread, formatCount, openStream } from '../store.js'

const subtitle = computed(() => {
  if (state.stream === 'starred') return null
  const n = streamUnread.value
  return n ? `${formatCount(n)} new item${n === 1 ? '' : 's'}` : 'No new items'
})
</script>

<template>
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
      <template v-else-if="subtitle && state.theme !== 'classic'">{{ subtitle }}</template>
      <span v-if="streamFeed?.lastError" class="stream-error">
        &nbsp;&middot; Error fetching this feed: {{ streamFeed.lastError }}
      </span>
    </div>
    <Toolbar v-if="state.theme === 'classic'" classic />
  </div>
</template>
