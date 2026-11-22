<script setup>
import { dismissToast, toasts } from '../stores/toasts.js'
</script>

<template>
  <div class="toasts" aria-live="polite">
    <TransitionGroup name="toast">
      <div v-for="toast in toasts" :key="toast.id" class="toast" :class="toast.kind" role="status">
        <div class="body">
          <strong>{{ toast.title }}</strong>
          <span v-if="toast.message" class="message">{{ toast.message }}</span>
        </div>
        <button class="close" aria-label="Dismiss" @click="dismissToast(toast.id)">×</button>
      </div>
    </TransitionGroup>
  </div>
</template>

<style scoped>
.toasts {
  position: fixed;
  right: 16px;
  bottom: 16px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: min(360px, calc(100vw - 32px));
  z-index: 100;
}
.toast {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 12px 12px 12px 14px;
  border-radius: var(--radius);
  background: var(--surface);
  border: 1px solid var(--border);
  border-left: 5px solid var(--accent);
  box-shadow: 0 6px 20px rgba(0, 0, 0, 0.15);
}
.toast.down, .toast.error { border-left-color: var(--down); }
.toast.up { border-left-color: var(--up); }
.body { flex: 1; display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.message { color: var(--text-muted); font-size: 0.9rem; word-break: break-word; }
.close { border: 0; background: none; padding: 0 4px; font-size: 1.2rem; line-height: 1; color: var(--text-muted); }

.toast-enter-active, .toast-leave-active { transition: all 0.2s ease; }
.toast-enter-from, .toast-leave-to { opacity: 0; transform: translateX(20px); }
</style>
