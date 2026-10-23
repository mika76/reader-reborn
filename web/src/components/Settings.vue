<script setup>
import { ref, computed } from 'vue'
import FeedIcon from './FeedIcon.vue'
import Icon from './Icon.vue'
import Dropdown from './Dropdown.vue'
import {
  state, feedsIn, openSettings, openStream, renameFeed, unsubscribe, setFolder,
  newFolder, renameFolder, deleteFolder, importOpml, setTheme, setView, setOrder,
} from '../store.js'
import { shortDate } from '../format.js'

const tabs = [
  ['preferences', 'Preferences'],
  ['subscriptions', 'Subscriptions'],
  ['folders', 'Folders'],
  ['import', 'Import/Export'],
  ['goodies', 'Goodies'],
]
const filter = ref('')

// Reader's "Subscribe as you surf" bookmarklet: opens Reader with the current page in the subscribe box
const bookmarklet = `javascript:void(location.href='${location.origin}/#subscribe/'+encodeURIComponent(location.href))`
const file = ref(null)

const feeds = computed(() => {
  const f = filter.value.trim().toLowerCase()
  return f ? state.feeds.filter(x => x.title.toLowerCase().includes(f) || x.url.toLowerCase().includes(f)) : state.feeds
})

async function upload() {
  const f = file.value?.files?.[0]
  if (!f) return
  await importOpml(await f.text())
  file.value.value = ''
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
    </div>

    <div v-else-if="state.settingsTab === 'subscriptions'" class="tab-body">
      <div class="settings-row">
        <input v-model="filter" class="text-input" placeholder="Filter by name or URL">
        <span class="settings-count">{{ state.feeds.length }} subscriptions</span>
      </div>
      <table class="settings-table">
        <tr v-for="feed in feeds" :key="feed.id">
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
        <button class="button" @click="upload">Upload</button>
      </p>
      <h3>Export your subscriptions</h3>
      <p>Download your subscriptions as an OPML file: <a href="/api/opml" download="subscriptions.opml">subscriptions.opml</a></p>
    </div>
  </div>
</template>
