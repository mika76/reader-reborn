<script setup>
import { computed } from 'vue'
import Icon from './Icon.vue'
import Dropdown from './Dropdown.vue'
import {
  state, streamFeed, streamFolder, streamUnread, formatCount,
  refresh, markAllRead, setUnreadOnly, setSort, setView, setTheme, move, openSettings,
  renameFeed, unsubscribe, setFolder, newFolder, renameFolder, deleteFolder,
} from '../store.js'

// classic: the 2008 layout, where the toolbar sits in the blue header above the entries
defineProps({ classic: Boolean })

const markItems = [
  { label: 'All items', action: () => markAllRead() },
  { separator: true },
  { label: 'Items older than a day', action: () => markAllRead(1) },
  { label: 'Items older than a week', action: () => markAllRead(7) },
  { label: 'Items older than two weeks', action: () => markAllRead(14) },
]

const showLabel = computed(() =>
  state.unreadOnly ? `${streamUnread.value ? formatCount(streamUnread.value) + ' ' : ''}New items` : 'All items')

const showItems = computed(() => [
  { label: 'All items', checked: !state.unreadOnly, action: () => setUnreadOnly(false) },
  { label: streamUnread.value != null ? `${formatCount(streamUnread.value)} new items` : 'New items', checked: state.unreadOnly, action: () => setUnreadOnly(true) },
])

const sortItems = () => [
  { label: 'Sort by newest', checked: state.sort === 'newest', action: () => setSort('newest') },
  { label: 'Sort by oldest', checked: state.sort === 'oldest', action: () => setSort('oldest') },
]

const settingsItems = computed(() => {
  const feed = streamFeed.value
  if (feed) {
    return [
      ...sortItems(),
      { separator: true },
      { label: 'Rename subscription...', action: () => renameFeed(feed) },
      { label: 'Unsubscribe', action: () => unsubscribe(feed) },
      { separator: true },
      { header: 'Folders' },
      ...state.folders.map(f => ({
        label: f.name, checked: feed.folderIds.includes(f.id),
        action: () => setFolder(feed, f.id, !feed.folderIds.includes(f.id)),
      })),
      { label: 'New folder...', action: () => newFolder(feed) },
    ]
  }
  if (streamFolder.value) {
    const folder = streamFolder.value
    return [
      ...sortItems(),
      { separator: true },
      { label: 'Rename folder...', action: () => renameFolder(folder) },
      { label: 'Delete folder', action: () => deleteFolder(folder) },
    ]
  }
  return sortItems()
})

const settingsLabel = computed(() =>
  streamFeed.value ? 'Feed settings...' : streamFolder.value ? 'Folder settings...' : 'View settings...')

const gearItems = computed(() => [
  { label: 'Reader settings', action: () => openSettings('subscriptions') },
  { label: 'Manage folders', action: () => openSettings('folders') },
  { label: 'Import/Export', action: () => openSettings('import') },
  { separator: true },
  { header: 'Theme' },
  { label: 'Classic (2008)', checked: state.theme === 'classic', action: () => setTheme('classic') },
  { label: 'Modern (2011)', checked: state.theme === 'modern', action: () => setTheme('modern') },
  { separator: true },
  { label: 'Keyboard shortcuts', action: () => (state.help = true) },
])
</script>

<template>
  <div v-if="classic" class="toolbar classic-toolbar">
    <span v-if="state.stream !== 'starred'" class="show-filter">
      Show:
      <a v-if="state.unreadOnly" class="show-current">{{ showItems[1].label }}</a>
      <a v-else @click="setUnreadOnly(true)">{{ showItems[1].label }}</a>
      -
      <a v-if="!state.unreadOnly" class="show-current">all items</a>
      <a v-else @click="setUnreadOnly(false)">all items</a>
    </span>
    <div class="button-group">
      <button class="button" title="Mark all as read (shift+a)" @click="markAllRead()">Mark all as read</button>
      <Dropdown :items="markItems" class="split" />
    </div>
    <button class="button" title="Refresh (r)" @click="refresh">Refresh</button>
    <Dropdown :label="settingsLabel" :items="settingsItems" />
    <div class="toolbar-right view-tabs">
      <a :class="{ selected: state.view === 'expanded' }" title="Expanded view (1)" @click="setView('expanded')">Expanded</a>
      <a :class="{ selected: state.view === 'list' }" title="List view (2)" @click="setView('list')">List</a>
    </div>
  </div>

  <div v-else class="toolbar">
    <template v-if="state.page === 'reader'">
      <button class="button icon-button" title="Refresh (r)" @click="refresh"><Icon name="refresh" /></button>
      <div class="button-group">
        <button class="button" title="Mark all as read (shift+a)" @click="markAllRead()">Mark all as read</button>
        <Dropdown :items="markItems" class="split" />
      </div>
      <Dropdown v-if="state.stream !== 'starred'" :label="showLabel" :items="showItems" />
      <Dropdown :label="settingsLabel" :items="settingsItems" />
    </template>

    <div class="toolbar-right">
      <template v-if="state.page === 'reader'">
        <div class="button-group">
          <button class="button icon-button" :class="{ active: state.view === 'expanded' }" title="Expanded view (1)"
                  @click="setView('expanded')"><Icon name="expanded" /></button>
          <button class="button icon-button" :class="{ active: state.view === 'list' }" title="List view (2)"
                  @click="setView('list')"><Icon name="list" /></button>
        </div>
        <div class="button-group">
          <button class="button icon-button" title="Previous item (k)" @click="move(-1)"><Icon name="up" /></button>
          <button class="button icon-button" title="Next item (j)" @click="move(1)"><Icon name="down" /></button>
        </div>
      </template>
      <Dropdown :items="gearItems" right class="gear"><Icon name="gear" /></Dropdown>
    </div>
  </div>
</template>
