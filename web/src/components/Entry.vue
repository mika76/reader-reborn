<script setup>
import { computed } from 'vue'
import Icon from './Icon.vue'
import FeedIcon from './FeedIcon.vue'
import { sanitize, shortDate, longDate, enclosureKind, enclosureLabel } from '../format.js'
import { state, feedById, select, toggleStar, setKeepUnread, openStream } from '../store.js'

const props = defineProps({ item: Object })

const feed = computed(() => feedById.value.get(props.item.feedId))
const current = computed(() => state.currentId === props.item.id)
const open = computed(() => state.view === 'expanded' || state.openId === props.item.id)
const body = computed(() => (open.value ? sanitize(props.item.content) : ''))
const mailto = computed(() =>
  `mailto:?subject=${encodeURIComponent(props.item.title)}&body=${encodeURIComponent(props.item.url || '')}`)
const showSource = computed(() => !state.stream.startsWith('feed:'))
// skip image attachments the post already shows
const enclosures = computed(() => (props.item.enclosures ?? [])
  .filter(e => !(enclosureKind(e) === 'image' && props.item.content?.includes(e.url))))
</script>

<template>
  <div :id="'entry-' + item.id" class="entry"
       :class="{ read: item.isRead, current, open, 'entry-list': state.view === 'list', 'entry-expanded': state.view === 'expanded' }">

    <!-- List view: one collapsed row -->
    <div v-if="state.view === 'list'" class="entry-row" @click="select(item, { open: true, toggle: true, scroll: false })">
      <span class="entry-star" :class="{ starred: item.isStarred }" @click.stop="toggleStar(item)">
        <Icon :name="item.isStarred ? 'star-on' : 'star'" />
      </span>
      <span v-if="showSource" class="entry-source">{{ item.feedTitle }}</span>
      <span class="entry-secondary">
        <span class="entry-row-title">{{ item.title }}</span>
        <span v-if="item.summary" class="entry-snippet"> - {{ item.summary }}</span>
      </span>
      <a v-if="item.url" class="entry-original" :href="item.url" target="_blank" rel="noopener" title="Open in a new window"
         @click.stop><Icon name="external" /></a>
      <span class="entry-date" :title="longDate(item.published)">{{ shortDate(item.published) }}</span>
    </div>

    <!-- Expanded view, or the opened entry in list view -->
    <div v-if="open" class="entry-container" @click="!current && select(item, { scroll: false })">
      <div class="entry-header">
        <span class="entry-date" :title="longDate(item.published)">{{ longDate(item.published) }}</span>
        <h2 class="entry-title">
          <span v-if="state.view === 'expanded'" class="entry-star" :class="{ starred: item.isStarred }" @click.stop="toggleStar(item)">
            <Icon :name="item.isStarred ? 'star-on' : 'star'" />
          </span>
          <a v-if="item.url" :href="item.url" target="_blank" rel="noopener" class="entry-title-link">{{ item.title }}</a>
          <span v-else>{{ item.title }}</span>
        </h2>
        <div class="entry-author">
          <FeedIcon :feed="feed" />
          from <a class="entry-source-title" @click.stop="openStream('feed:' + item.feedId)">{{ item.feedTitle }}</a>
          <template v-if="item.author"> by <span class="entry-author-name">{{ item.author }}</span></template>
        </div>
      </div>
      <div class="entry-body" v-html="body" />
      <div v-if="enclosures.length" class="entry-enclosures">
        <div v-for="e in enclosures" :key="e.url" class="enclosure">
          <audio v-if="enclosureKind(e) === 'audio'" controls preload="none" :src="e.url" />
          <video v-else-if="enclosureKind(e) === 'video'" controls preload="none" :src="e.url" />
          <img v-else-if="enclosureKind(e) === 'image'" :src="e.url" alt="" loading="lazy">
          <a :href="e.url" target="_blank" rel="noopener" class="enclosure-link">{{ enclosureLabel(e) }}</a>
        </div>
      </div>
      <div class="entry-actions" @click.stop>
        <span class="entry-action star" :class="{ starred: item.isStarred }" @click="toggleStar(item)">
          <Icon :name="item.isStarred ? 'star-on' : 'star'" />{{ item.isStarred ? 'Remove star' : 'Add star' }}
        </span>
        <a class="entry-action" :href="mailto"><Icon name="mail" />Email</a>
        <label class="entry-action">
          <input type="checkbox" :checked="item.keepUnread" @change="setKeepUnread(item, $event.target.checked)">Keep unread
        </label>
        <a v-if="item.url" class="entry-action" :href="item.url" target="_blank" rel="noopener">
          <Icon name="external" />View original
        </a>
      </div>
    </div>
  </div>
</template>
