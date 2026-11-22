<script setup>
import { computed, onMounted, onUnmounted, ref } from 'vue'
import AddUrlForm from '../components/AddUrlForm.vue'
import ConfirmButton from '../components/ConfirmButton.vue'
import StatusChip from '../components/StatusChip.vue'
import { api } from '../api/client.js'
import { showToast } from '../stores/toasts.js'
import { POLL_INTERVAL_MS, refreshUrls, urlStatus } from '../stores/urlStatus.js'
import { formatMs, formatPercent, statusText, timeAgo } from '../utils/format.js'

const busy = ref(new Set())
const now = ref(Date.now())
let clock = null

onMounted(() => {
  refreshUrls()
  clock = setInterval(() => (now.value = Date.now()), 10_000)
})
onUnmounted(() => clearInterval(clock))

// Down first, then not-yet-checked, then up, so problems are visible at a glance.
const order = (u) => (!u.latestCheck ? 1 : u.latestCheck.isUp ? 2 : 0)
const sortedUrls = computed(() => [...urlStatus.urls].sort((a, b) => order(a) - order(b) || a.id - b.id))

const counts = computed(() => ({
  total: urlStatus.urls.length,
  up: urlStatus.urls.filter((u) => u.latestCheck?.isUp).length,
  down: urlStatus.urls.filter((u) => u.latestCheck && !u.latestCheck.isUp).length,
}))

async function withBusy(id, action) {
  busy.value = new Set(busy.value).add(id)
  try {
    await action()
  } catch (e) {
    showToast('error', 'Something went wrong', e.message)
  } finally {
    const next = new Set(busy.value)
    next.delete(id)
    busy.value = next
    await refreshUrls()
  }
}

// Check a new URL straight away so it doesn't sit at "Pending" until the next 5-minute run.
const onAdded = (url) => withBusy(url.id, () => api.checkNow(url.id))
const checkNow = (url) => withBusy(url.id, () => api.checkNow(url.id))
const remove = (url) => withBusy(url.id, () => api.deleteUrl(url.id))
</script>

<template>
  <main class="page">
    <div class="page-header">
      <div>
        <h1>Dashboard</h1>
        <p class="muted small" v-if="urlStatus.loaded">
          {{ counts.total }} URLs ·
          <span class="up-text">{{ counts.up }} up</span> ·
          <span class="down-text">{{ counts.down }} down</span>
          <template v-if="urlStatus.lastUpdated"> · refreshed {{ timeAgo(urlStatus.lastUpdated.toISOString(), now) }}, every {{ POLL_INTERVAL_MS / 1000 }}s</template>
        </p>
      </div>
    </div>

    <AddUrlForm @added="onAdded" />

    <p v-if="urlStatus.error" class="card error-text">{{ urlStatus.error }}</p>

    <section class="card table-card">
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Status</th>
              <th>URL</th>
              <th>HTTP</th>
              <th class="num">Response time</th>
              <th class="num">Uptime 24h</th>
              <th>Last checked</th>
              <th />
            </tr>
          </thead>
          <tbody>
            <tr v-for="url in sortedUrls" :key="url.id">
              <td><StatusChip :check="url.latestCheck" /></td>
              <td class="url-cell">
                <RouterLink :to="`/urls/${url.id}`" class="break">{{ url.name ?? url.url }}</RouterLink>
                <div v-if="url.name" class="muted small break">{{ url.url }}</div>
                <div v-if="url.latestCheck?.error" class="error-detail small break">{{ url.latestCheck.error }}</div>
              </td>
              <td class="mono">{{ statusText(url.latestCheck) }}</td>
              <td class="num">{{ formatMs(url.latestCheck?.responseTimeMs) }}</td>
              <td class="num">{{ formatPercent(url.uptime24hPercent) }}</td>
              <td class="nowrap muted small">{{ timeAgo(url.latestCheck?.checkedAt, now) }}</td>
              <td>
                <div class="actions">
                  <button class="small" :disabled="busy.has(url.id)" @click="checkNow(url)">
                    {{ busy.has(url.id) ? 'Checking…' : 'Check now' }}
                  </button>
                  <ConfirmButton :disabled="busy.has(url.id)" @confirm="remove(url)" />
                </div>
              </td>
            </tr>
            <tr v-if="urlStatus.loaded && sortedUrls.length === 0">
              <td colspan="7" class="empty">No URLs yet. Add one above to start monitoring.</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </main>
</template>

<style scoped>
.table-card { padding: 0; }
.url-cell { max-width: 380px; }
.error-detail { color: var(--down); margin-top: 2px; }
.actions { display: flex; gap: 6px; justify-content: flex-end; }
.up-text { color: var(--up); font-weight: 600; }
.down-text { color: var(--down); font-weight: 600; }
</style>
