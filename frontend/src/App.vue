<script setup>
import { onMounted, onUnmounted } from 'vue'
import ToastNotifications from './components/ToastNotifications.vue'
import { startPolling, stopPolling, urlStatus } from './stores/urlStatus.js'

onMounted(startPolling)
onUnmounted(stopPolling)
</script>

<template>
  <header class="topbar">
    <div class="topbar-inner">
      <RouterLink to="/" class="brand">URL Monitor</RouterLink>
      <nav>
        <RouterLink to="/" :class="{ active: $route.name === 'dashboard' || $route.name === 'url' }">Dashboard</RouterLink>
        <RouterLink to="/alerts" active-class="active">Alerts</RouterLink>
      </nav>
      <span v-if="urlStatus.error" class="badge down api-status" :title="urlStatus.error">API unreachable</span>
    </div>
  </header>

  <RouterView />

  <ToastNotifications />
</template>

<style scoped>
.topbar {
  background: var(--surface);
  border-bottom: 1px solid var(--border);
  position: sticky;
  top: 0;
  z-index: 10;
}
.topbar-inner {
  max-width: 1100px;
  margin: 0 auto;
  padding: 0 16px;
  display: flex;
  align-items: center;
  gap: 24px;
  height: 52px;
}
.brand { font-weight: 700; color: var(--text); font-size: 1.05rem; }
.brand:hover { text-decoration: none; }
nav { display: flex; gap: 4px; }
nav a {
  color: var(--text-muted);
  padding: 6px 10px;
  border-radius: 6px;
}
nav a:hover { background: var(--surface-2); text-decoration: none; }
nav a.active { color: var(--text); background: var(--surface-2); font-weight: 600; }
.api-status { margin-left: auto; }
</style>
