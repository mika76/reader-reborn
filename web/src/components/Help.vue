<script setup>
import { state } from '../store.js'

const sections = [
  ['Navigation', [
    ['j / k', 'item down / up'],
    ['space', 'page down'],
    ['shift + space', 'page up'],
    ['n / p', 'scan down / up (list)'],
    ['shift + j / k', 'next / previous subscription'],
  ]],
  ['Application', [
    ['r', 'refresh'],
    ['u', 'toggle full screen mode'],
    ['1', 'switch to expanded view'],
    ['2', 'switch to list view'],
    ['/', 'move cursor to search box'],
    ['a', 'add a subscription'],
    ['?', 'show keyboard shortcuts help'],
  ]],
  ['Acting on items', [
    ['s', 'star item'],
    ['m', 'mark item as read / unread'],
    ['shift + a', 'mark all as read'],
    ['v', 'view original'],
    ['o / enter', 'expand / collapse item (list)'],
  ]],
  ['Jumping', [
    ['g then a', 'go to all items'],
    ['g then s', 'go to starred items'],
    ['g then t', 'go to settings'],
  ]],
]
</script>

<template>
  <div class="help-overlay" @click.self="state.help = false">
    <div class="help">
      <div class="help-title">
        Keyboard shortcuts
        <a class="help-close" @click="state.help = false">Close</a>
      </div>
      <div class="help-columns">
        <table v-for="[title, keys] in sections" :key="title" class="help-section">
          <tr><th colspan="2">{{ title }}</th></tr>
          <tr v-for="[key, what] in keys" :key="key">
            <td class="help-key">{{ key }}:</td>
            <td>{{ what }}</td>
          </tr>
        </table>
      </div>
    </div>
  </div>
</template>
