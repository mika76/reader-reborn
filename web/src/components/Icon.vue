<script setup>
// Small inline icons in the style of the 2011 Reader sprite sheet.
defineProps({ name: String })
</script>

<template>
  <svg class="icon" :class="'icon-' + name" viewBox="0 0 16 16" aria-hidden="true">
    <template v-if="name === 'star' || name === 'star-on'">
      <polygon points="8,1.3 10,5.8 14.8,6.2 11.1,9.4 12.3,14.2 8,11.6 3.7,14.2 4.9,9.4 1.2,6.2 6,5.8" />
    </template>
    <path v-else-if="name === 'refresh'" d="M13 8a5 5 0 1 1-1.6-3.7M13 2.5v3h-3" />
    <path v-else-if="name === 'up'" d="M3.5 10.5 8 6l4.5 4.5" />
    <path v-else-if="name === 'down'" d="M3.5 5.5 8 10l4.5-4.5" />
    <template v-else-if="name === 'gear'">
      <path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4" />
      <circle cx="8" cy="8" r="3.4" />
    </template>
    <path v-else-if="name === 'expanded'" d="M2 3h12M2 6h12M2 9h12M2 13h12" class="thick" />
    <path v-else-if="name === 'list'" d="M2 3h12M2 6h12M2 9h12M2 12h12" />
    <template v-else-if="name === 'search'">
      <circle cx="6.5" cy="6.5" r="4.2" /><path d="M9.6 9.6 14 14" />
    </template>
    <path v-else-if="name === 'triangle-open'" d="M4 6h8l-4 5z" class="solid" />
    <path v-else-if="name === 'triangle-closed'" d="M6 4v8l5-4z" class="solid" />
    <template v-else-if="name === 'folder'">
      <path d="M1.5 4V13h13V5.5H7.5L6 4z" />
    </template>
    <template v-else-if="name === 'feed'">
      <rect x="2" y="2" width="12" height="12" rx="1.5" />
      <path d="M5 5h6M5 8h6M5 11h4" />
    </template>
    <path v-else-if="name === 'external'" d="M9 2.5h4.5V7M13.5 2.5 7.5 8.5M12 9.5v4H2.5V4h4" />
    <path v-else-if="name === 'trends'" d="M1.5 14.5h13M3 12V9M6.5 12V5M10 12V7M13.5 12V3" class="thick" />
    <template v-else-if="name === 'mail'">
      <rect x="1.5" y="3.5" width="13" height="9" /><path d="M1.5 3.5 8 9l6.5-5.5" />
    </template>
  </svg>
</template>
