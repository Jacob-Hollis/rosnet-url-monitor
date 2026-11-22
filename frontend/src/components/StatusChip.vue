<script setup>
import { computed } from 'vue'

// Red/green chip for the most recent check. Grey when the URL hasn't been checked yet.
const props = defineProps({
  check: { type: Object, default: null },
})

const state = computed(() => (!props.check ? 'unknown' : props.check.isUp ? 'up' : 'down'))
const label = computed(() => ({ up: 'Up', down: 'Down', unknown: 'Pending' })[state.value])
const title = computed(() => props.check?.error ?? (state.value === 'unknown' ? 'Not checked yet' : ''))
</script>

<template>
  <span class="chip" :class="state" :title="title">
    <span class="dot" aria-hidden="true" />
    {{ label }}
  </span>
</template>

<style scoped>
.chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 10px 3px 8px;
  border-radius: 999px;
  font-size: 0.85rem;
  font-weight: 600;
  white-space: nowrap;
}
.dot { width: 8px; height: 8px; border-radius: 50%; background: currentColor; }
.up { background: var(--up-bg); color: var(--up); }
.down { background: var(--down-bg); color: var(--down); }
.unknown { background: var(--unknown-bg); color: var(--unknown); }
</style>
