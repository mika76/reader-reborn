<script setup>
// Reader's Trends page: which subscriptions post the most, and which have gone quiet
import { ref, computed, watch } from 'vue'
import FeedIcon from './FeedIcon.vue'
import * as api from '../api.js'
import { state, feedById, openTrends, openStream, openSettings, bulkUnsubscribe } from '../store.js'

const trends = ref(null)
const selected = ref(new Set())

async function load() {
  trends.value = await api.get('/api/trends').catch(() => null)
  selected.value = new Set()
}
load()
// unsubscribing elsewhere (or here) changes the lists
watch(() => state.feeds.length, load)

const n = v => (v ?? 0).toLocaleString()
const date = t => new Date(t * 1000).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })

const selectedIds = computed(() => trends.value?.inactive.filter(f => selected.value.has(f.id)).map(f => f.id) ?? [])
const selectAll = () => (selected.value = new Set(trends.value.inactive.map(f => f.id)))
const selectNone = () => (selected.value = new Set())
function toggle(id, on) {
  const next = new Set(selected.value)
  if (on) next.add(id)
  else next.delete(id)
  selected.value = next
}
</script>

<template>
  <div class="settings trends">
    <div class="settings-header">
      <h1>Trends</h1>
      <a class="settings-back" @click="openStream(state.stream)">&laquo; Back to Reader</a>
    </div>
    <p v-if="trends" class="trends-summary">
      From your <b>{{ n(trends.subscriptions) }}</b> subscriptions, over the last 30 days you received
      <b>{{ n(trends.items) }}</b> items.
    </p>

    <div class="tabs">
      <a class="tab" :class="{ selected: state.trendsTab === 'active' }" @click="openTrends('active')">Most active</a>
      <a class="tab" :class="{ selected: state.trendsTab === 'inactive' }" @click="openTrends('inactive')">
        Inactive<template v-if="trends"> ({{ n(trends.inactive.length) }})</template>
      </a>
    </div>

    <p v-if="!trends" class="trends-note">Loading...</p>

    <div v-else-if="state.trendsTab === 'active'" class="tab-body">
      <p class="trends-note">Subscriptions with the most items in the last 30 days.</p>
      <table class="settings-table trends-table">
        <tr><th>Subscription</th><th>Items</th><th>Items per day</th></tr>
        <tr v-for="f in trends.mostActive" :key="f.id">
          <td class="col-name">
            <FeedIcon :feed="feedById.get(f.id)" />
            <a class="sub-title" @click="openStream('feed:' + f.id)">{{ f.title }}</a>
          </td>
          <td class="num">{{ n(f.items) }}</td>
          <td class="num">{{ (f.items / 30).toFixed(1) }}</td>
        </tr>
        <tr v-if="!trends.mostActive.length"><td colspan="3" class="settings-empty">Nothing new in the last 30 days.</td></tr>
      </table>
    </div>

    <div v-else class="tab-body">
      <p class="trends-note">
        These subscriptions haven't been updated in over 30 days. You may want to unsubscribe from the ones
        that have stopped publishing, or <a @click="openSettings('subscriptions')">manage your subscriptions</a>.
      </p>
      <div class="settings-row select-row">
        <span>Select: <a @click="selectAll">All</a>, <a @click="selectNone">None</a></span>
        <span v-if="selectedIds.length" class="settings-count">{{ n(selectedIds.length) }} selected</span>
        <button class="button" :disabled="!selectedIds.length" @click="bulkUnsubscribe(selectedIds)">Unsubscribe</button>
      </div>
      <table class="settings-table trends-table">
        <tr v-for="f in trends.inactive" :key="f.id" :class="{ checked: selected.has(f.id) }">
          <td class="col-check">
            <input type="checkbox" :checked="selected.has(f.id)" @change="toggle(f.id, $event.target.checked)">
          </td>
          <td class="col-name">
            <FeedIcon :feed="feedById.get(f.id)" />
            <a class="sub-title" @click="openStream('feed:' + f.id)">{{ f.title }}</a>
          </td>
          <td class="col-fetched">{{ f.lastItem ? 'Last updated ' + date(f.lastItem) : 'Never updated' }}</td>
        </tr>
        <tr v-if="!trends.inactive.length"><td colspan="3" class="settings-empty">All your subscriptions are active.</td></tr>
      </table>
    </div>
  </div>
</template>
