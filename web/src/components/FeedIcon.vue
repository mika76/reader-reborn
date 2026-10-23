<script setup>
import { ref, watch } from 'vue'
import Icon from './Icon.vue'

const props = defineProps({ feed: Object })
const failed = ref(false)
watch(() => props.feed?.id, () => (failed.value = false))
</script>

<template>
  <img v-if="feed?.hasIcon && !failed" class="favicon" :src="`/api/feeds/${feed.id}/icon`" alt="" @error="failed = true">
  <Icon v-else name="feed" class="favicon favicon-default" />
</template>
