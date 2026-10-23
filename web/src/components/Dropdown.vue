<script setup>
// A Kennedy-style button that opens a menu. Menu items are passed as
// [{ label, action, checked, separator, disabled }].
import { ref, onMounted, onBeforeUnmount } from 'vue'

defineProps({ label: String, items: Array, right: Boolean })
const open = ref(false)
const el = ref(null)

const close = e => { if (!el.value?.contains(e.target)) open.value = false }
onMounted(() => document.addEventListener('mousedown', close))
onBeforeUnmount(() => document.removeEventListener('mousedown', close))

function run(item) {
  if (item.disabled) return
  open.value = false
  item.action?.()
}
</script>

<template>
  <div ref="el" class="dropdown" :class="{ open }">
    <button class="button" :class="{ active: open }" @click="open = !open">
      <slot>{{ label }}</slot><span class="caret" />
    </button>
    <ul v-if="open" class="menu" :class="{ right }">
      <template v-for="(item, i) in items" :key="i">
        <li v-if="item.separator" class="menu-separator" />
        <li v-else-if="item.header" class="menu-header">{{ item.header }}</li>
        <li v-else class="menu-item" :class="{ checked: item.checked, disabled: item.disabled }" @click="run(item)">
          {{ item.label }}
        </li>
      </template>
    </ul>
  </div>
</template>
