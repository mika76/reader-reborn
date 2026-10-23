<script setup>
import { ref, computed, watch } from 'vue'
import FeedIcon from './FeedIcon.vue'
import Icon from './Icon.vue'
import Dropdown from './Dropdown.vue'
import {
  state, feedsIn, openSettings, openStream, renameFeed, unsubscribe, setFolder,
  newFolder, renameFolder, deleteFolder, bulkUnsubscribe, bulkFolder, bulkNewFolder, importOpml, importStarred, setTheme, setView, setOrder,
} from '../store.js'
import { shortDate, formatBytes } from '../format.js'
import * as api from '../api.js'

const tabs = [
  ['preferences', 'Preferences'],
  ['subscriptions', 'Subscriptions'],
  ['folders', 'Folders'],
  ['import', 'Import/Export'],
  ['goodies', 'Goodies'],
]
const filter = ref('')

async function setSaveAttachments(on) {
  const r = await api.patch('/api/settings', { saveStarredAttachments: on }).catch(() => null)
  if (r && attachments.value) attachments.value.enabled = r.saveStarredAttachments
}

// Saved post images, shown on the Preferences tab
const imageStats = ref(null)
const attachments = ref(null) // { enabled, saved, bytes, pending }
watch(() => state.settingsTab, async tab => {
  if (tab !== 'preferences') return
  imageStats.value = await api.get('/api/images/stats').catch(() => null)
  attachments.value = (await api.get('/api/status').catch(() => null))?.attachments ?? null
}, { immediate: true })

// Reader's "Subscribe as you surf" bookmarklet: opens Reader with the current page in the subscribe box
const bookmarklet = `javascript:void(location.href='${location.origin}/#subscribe/'+encodeURIComponent(location.href))`
const file = ref(null)

const feeds = computed(() => {
  const f = filter.value.trim().toLowerCase()
  return f ? state.feeds.filter(x => x.title.toLowerCase().includes(f) || x.url.toLowerCase().includes(f)) : state.feeds
})

// Checked subscriptions, for "More actions..." (Select: All picks the ones the filter shows)
const selected = ref(new Set())
const selectedIds = computed(() => state.feeds.filter(f => selected.value.has(f.id)).map(f => f.id))
const selectAll = () => (selected.value = new Set(feeds.value.map(f => f.id)))
const selectNone = () => (selected.value = new Set())
function toggleSelected(id, on) {
  const next = new Set(selected.value)
  if (on) next.add(id)
  else next.delete(id)
  selected.value = next
}

const moreActions = computed(() => {
  const ids = selectedIds.value
  return [
    { label: 'Unsubscribe', disabled: !ids.length, action: async () => (await bulkUnsubscribe(ids)) && selectNone() },
    { separator: true },
    { header: 'Add to folder' },
    ...state.folders.map(f => ({ label: f.name, disabled: !ids.length, action: () => bulkFolder(ids, f.id, true) })),
    { label: 'New folder...', disabled: !ids.length, action: () => bulkNewFolder(ids) },
    ...(state.folders.length ? [
      { separator: true },
      { header: 'Remove from folder' },
      ...state.folders.map(f => ({ label: f.name, disabled: !ids.length, action: () => bulkFolder(ids, f.id, false) })),
    ] : []),
  ]
})

const starredFile = ref(null)

async function upload(input, importer) {
  const f = input?.files?.[0]
  if (!f) return
  await importer(await f.text())
  input.value = ''
}

const folderLabel = feed =>
  feed.folderIds.map(id => state.folders.find(f => f.id === id)?.name).filter(Boolean).join(', ') || 'No folder'

const folderItems = feed => [
  ...state.folders.map(f => ({
    label: f.name, checked: feed.folderIds.includes(f.id),
    action: () => setFolder(feed, f.id, !feed.folderIds.includes(f.id)),
  })),
  ...(state.folders.length ? [{ separator: true }] : []),
  { label: 'New folder...', action: () => newFolder(feed) },
]
</script>

<template>
  <div class="settings">
    <div class="settings-header">
      <h1>Settings</h1>
      <a class="settings-back" @click="openStream(state.stream)">&laquo; Back to Reader</a>
    </div>
    <div class="tabs">
      <a v-for="[id, label] in tabs" :key="id" class="tab" :class="{ selected: state.settingsTab === id }"
         @click="openSettings(id)">{{ label }}</a>
    </div>

    <div v-if="state.settingsTab === 'preferences'" class="tab-body preferences">
      <h3>Theme</h3>
      <label><input type="radio" :checked="state.theme === 'classic'" @change="setTheme('classic')">
        Classic &mdash; the light blue look Reader had from 2008 until the 2011 redesign</label>
      <label><input type="radio" :checked="state.theme === 'modern'" @change="setTheme('modern')">
        Modern &mdash; the grey and red look from November 2011 until Reader closed in 2013</label>
      <h3>Subscription order</h3>
      <label><input type="radio" :checked="state.order === 'alpha'" @change="setOrder('alpha')"> Alphabetical</label>
      <label><input type="radio" :checked="state.order === 'manual'" @change="setOrder('manual')">
        Drag and drop &mdash; drag subscriptions and folders in the sidebar to arrange them</label>
      <h3>Start in</h3>
      <label><input type="radio" :checked="state.view === 'expanded'" @change="setView('expanded')"> Expanded view</label>
      <label><input type="radio" :checked="state.view === 'list'" @change="setView('list')"> List view</label>
      <h3>Saved images</h3>
      <p v-if="!imageStats">&nbsp;</p>
      <p v-else-if="!imageStats.enabled">
        Post images aren't being saved (the server was started with <code>READER_IMAGES=off</code>).
      </p>
      <p v-else>
        Reader keeps a copy of the pictures in your posts, so they still show if the original site goes away.<br>
        {{ imageStats.downloaded.toLocaleString() }} images saved ({{ formatBytes(imageStats.bytes) || '0 bytes' }})<template
          v-if="imageStats.pending">, {{ imageStats.pending.toLocaleString() }} still to download</template><template
          v-if="imageStats.failed">, {{ imageStats.failed.toLocaleString() }} couldn't be downloaded</template>.
      </p>
      <h3>Attachments of starred items</h3>
      <label v-if="attachments">
        <input type="checkbox" :checked="attachments.enabled" @change="setSaveAttachments($event.target.checked)">
        Save podcast episodes, videos and other attachments of the posts you star
      </label>
      <p v-if="attachments" class="pref-note">
        Saved copies still play if the original site goes away. Episodes are often 30&ndash;100 MB each.
        A copy is deleted when you unstar its post; turning this off stops new downloads but keeps the copies you have.<br>
        {{ attachments.saved.toLocaleString() }} saved ({{ formatBytes(attachments.bytes) || '0 bytes' }})<template
          v-if="attachments.enabled && attachments.pending">, {{ attachments.pending.toLocaleString() }} to download</template>.
      </p>
    </div>

    <div v-else-if="state.settingsTab === 'subscriptions'" class="tab-body">
      <div class="settings-row">
        <input v-model="filter" class="text-input" placeholder="Filter by name or URL">
        <span class="settings-count">{{ state.feeds.length }} subscriptions</span>
      </div>
      <div class="settings-row select-row">
        <span>Select: <a @click="selectAll">All</a>, <a @click="selectNone">None</a></span>
        <span v-if="selectedIds.length" class="settings-count">{{ selectedIds.length }} selected</span>
        <Dropdown label="More actions..." :items="moreActions" />
      </div>
      <table class="settings-table">
        <tr v-for="feed in feeds" :key="feed.id" :class="{ checked: selected.has(feed.id) }">
          <td class="col-check">
            <input type="checkbox" :checked="selected.has(feed.id)" @change="toggleSelected(feed.id, $event.target.checked)">
          </td>
          <td class="col-name">
            <FeedIcon :feed="feed" />
            <a class="sub-title" @click="openStream('feed:' + feed.id)">{{ feed.title }}</a>
            <div class="sub-url">{{ feed.url }}</div>
            <div v-if="feed.lastError" class="sub-error">Error: {{ feed.lastError }}</div>
          </td>
          <td class="col-fetched">{{ feed.lastFetched ? 'Updated ' + shortDate(feed.lastFetched) : 'Not fetched yet' }}</td>
          <td class="col-actions">
            <a @click="renameFeed(feed)">Rename</a>
            <Dropdown :label="folderLabel(feed)" :items="folderItems(feed)" right class="folder-picker" />
            <button class="button" @click="unsubscribe(feed)">Unsubscribe</button>
          </td>
        </tr>
      </table>
    </div>

    <div v-else-if="state.settingsTab === 'folders'" class="tab-body">
      <div class="settings-row"><button class="button" @click="newFolder()">New folder...</button></div>
      <table class="settings-table">
        <tr v-for="folder in state.folders" :key="folder.id">
          <td class="col-name">
            <Icon name="folder" class="favicon folder-icon" />
            <a class="sub-title" @click="openStream('folder:' + folder.id)">{{ folder.name }}</a>
            <div class="sub-url">{{ feedsIn(folder.id).length }} subscriptions</div>
          </td>
          <td class="col-actions">
            <a @click="renameFolder(folder)">Rename</a>
            <button class="button" @click="deleteFolder(folder)">Delete</button>
          </td>
        </tr>
        <tr v-if="!state.folders.length"><td class="settings-empty">You don't have any folders.</td></tr>
      </table>
    </div>

    <div v-else-if="state.settingsTab === 'goodies'" class="tab-body goodies">
      <h3>Subscribe as you surf</h3>
      <p>
        Drag this link to your browser's bookmarks bar:
        <a class="bookmarklet" :href="bookmarklet" @click.prevent>Subscribe...</a>
      </p>
      <p>
        When you're on a blog or news site you'd like to follow, click the bookmark. Reader opens with the
        page's address filled in; click <b>Add</b> to subscribe.
      </p>
    </div>

    <div v-else-if="state.settingsTab === 'import'" class="tab-body import">
      <h3>Import your subscriptions</h3>
      <p>Upload an OPML file exported from Google Reader (Takeout), Feedly or any other feed reader.</p>
      <p>
        <input ref="file" type="file" accept=".opml,.xml,text/xml,text/x-opml">
        <button class="button" @click="upload(file, importOpml)">Upload</button>
      </p>
      <h3>Import your starred items</h3>
      <p>
        Upload <b>starred.json</b> from your Google Reader Takeout archive. Items from feeds you no longer
        follow are kept with your starred items; they don't add subscriptions.
      </p>
      <p>
        <input ref="starredFile" type="file" accept=".json,application/json">
        <button class="button" @click="upload(starredFile, importStarred)">Upload</button>
      </p>
      <h3>Export</h3>
      <p>Your subscriptions as OPML: <a href="/api/opml" download="subscriptions.opml">subscriptions.opml</a></p>
      <p>Your starred items, in the same format as Takeout: <a href="/api/starred" download="starred.json">starred.json</a></p>
    </div>
  </div>
</template>
