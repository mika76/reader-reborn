<script setup>
// What the server is up to: not something Google Reader had, but useful when you run the server yourself.
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import * as api from '../api.js'
import { state } from '../store.js'
import { formatBytes } from '../format.js'

const status = ref(null)
const error = ref(null)
const now = ref(Date.now() / 1000)
let timer

async function load() {
  try {
    status.value = await api.get('/api/status')
    error.value = null
  } catch (e) {
    error.value = e.message
  }
  now.value = Date.now() / 1000
}

onMounted(() => {
  load()
  timer = setInterval(load, 2000)
})
onBeforeUnmount(() => clearInterval(timer))

const n = v => (v ?? 0).toLocaleString()

function duration(seconds) {
  seconds = Math.max(0, Math.round(seconds))
  if (seconds < 60) return `${seconds}s`
  const m = Math.floor(seconds / 60)
  if (m < 60) return `${m}m ${seconds % 60}s`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ${m % 60}m`
  return `${Math.floor(h / 24)}d ${h % 24}h`
}

const ago = t => (now.value - t < 5 ? 'just now' : `${duration(now.value - t)} ago`)
const time = t => new Date(t * 1000).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', second: '2-digit' })

const feedsLine = computed(() => {
  const s = status.value
  if (s.fetching) return `Checking feeds: ${n(s.fetching.done)} of ${n(s.fetching.total)} (${duration(now.value - s.fetching.started)} so far)`
  if (s.feeds.nextFetch) return `Idle. Next feed check in ${duration(s.feeds.nextFetch - now.value)}`
  return 'Idle'
})

const imagesLine = computed(() => {
  const i = status.value.images
  if (!i.enabled) return 'Image saving is turned off (READER_IMAGES=off)'
  if (i.filesToMove) return `Moving image files into reader-media.db: ${n(i.filesToMove)} to go`
  if (i.postsToScan) return `Looking for images in ${n(i.postsToScan)} posts; ${n(i.pending)} images to download`
  if (i.pending) return `Downloading images: ${n(i.pending)} to go`
  return 'All images saved'
})

const lastPass = computed(() => {
  const p = status.value.lastPass
  if (!p) return null
  return `Last check finished ${ago(p.finished)}: ${n(p.feeds)} feeds in ${duration(p.finished - p.started)}, ` +
    `${n(p.newItems)} new items` + (p.errors ? `, ${n(p.errors)} failed` : '')
})
</script>

<template>
  <div class="dialog-overlay" @click.self="state.statusOpen = false">
    <div class="dialog status-dialog" role="dialog" aria-label="Server status">
      <div class="dialog-title">
        Server status
        <a class="dialog-close" title="Close" @click="state.statusOpen = false">&times;</a>
      </div>

      <p v-if="error" class="status-error">Can't reach the server: {{ error }}</p>
      <p v-else-if="!status" class="status-loading">Loading...</p>

      <template v-if="status">
        <div class="status-now">
          <div>
            <b>{{ feedsLine }}</b>
            <div v-if="status.fetching" class="status-progress">
              <div :style="{ width: (100 * status.fetching.done / Math.max(1, status.fetching.total)) + '%' }" />
            </div>
          </div>
          <div><b>{{ imagesLine }}</b></div>
          <div v-if="lastPass" class="status-muted">{{ lastPass }}</div>
        </div>

        <div class="status-grid">
          <table>
            <tr><th colspan="2">Feeds</th></tr>
            <tr><td>Subscriptions</td><td>{{ n(status.feeds.subscribed) }}</td></tr>
            <tr><td>Due for a check</td><td>{{ n(status.feeds.due) }}</td></tr>
            <tr>
              <td>Failing</td>
              <td :class="{ bad: status.feeds.failing }">{{ n(status.feeds.failing) }}</td>
            </tr>
            <tr><td>Checked every</td><td>{{ status.server.intervalMinutes }} min</td></tr>
          </table>
          <table>
            <tr><th colspan="2">Posts</th></tr>
            <tr><td>Total</td><td>{{ n(status.posts.total) }}</td></tr>
            <tr><td>Unread</td><td>{{ n(status.posts.unread) }}</td></tr>
            <tr><td>Starred</td><td>{{ n(status.posts.starred) }}</td></tr>
            <tr><td>New in the last day</td><td>{{ n(status.posts.lastDay) }}</td></tr>
            <tr><td>Attachments</td><td>{{ n(status.posts.attachments) }}</td></tr>
          </table>
          <table>
            <tr><th colspan="2">Images</th></tr>
            <tr><td>Saved</td><td>{{ n(status.images.downloaded) }}</td></tr>
            <tr><td>Size</td><td>{{ formatBytes(status.images.bytes) || '0 bytes' }}</td></tr>
            <tr><td>To download</td><td>{{ n(status.images.pending) }}</td></tr>
            <tr><td>Couldn't download</td><td>{{ n(status.images.failed) }}</td></tr>
          </table>
          <table>
            <tr><th colspan="2">Server</th></tr>
            <tr><td>Running for</td><td>{{ duration(now - status.server.started) }}</td></tr>
            <tr><td>Memory</td><td>{{ formatBytes(status.server.memory) }}</td></tr>
            <tr><td>reader.db</td><td>{{ formatBytes(status.server.databaseBytes) }}</td></tr>
            <tr><td>reader-media.db</td><td>{{ formatBytes(status.server.mediaBytes) || '0 bytes' }}</td></tr>
            <tr><td>.NET</td><td>{{ status.server.dotnet }}</td></tr>
          </table>
        </div>

        <div class="status-recent-title">Recent activity</div>
        <ul class="status-recent">
          <li v-for="(entry, i) in status.recent" :key="i" :class="'kind-' + entry.kind">
            <span class="status-time">{{ time(entry.time) }}</span>{{ entry.text }}
          </li>
          <li v-if="!status.recent.length" class="status-muted">Nothing yet since the server started.</li>
        </ul>
        <div class="status-muted status-path">{{ status.server.database }}</div>
      </template>
    </div>
  </div>
</template>
