<script setup>
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ConfirmButton from '../components/ConfirmButton.vue'
import HistoryChart from '../components/HistoryChart.vue'
import HistoryTable from '../components/HistoryTable.vue'
import StatusChip from '../components/StatusChip.vue'
import { api } from '../api/client.js'
import { showToast } from '../stores/toasts.js'
import { refreshUrls } from '../stores/urlStatus.js'
import { formatDateTime, formatMs, formatPercent, statusText } from '../utils/format.js'

const props = defineProps({ id: { type: Number, required: true } })

const RANGES = ['1h', '6h', '24h', '7d', '30d']
const PAGE_SIZE = 50
const REFRESH_MS = 60_000

const route = useRoute()
const router = useRouter()

// View, metric and range live in the query string, so a refresh or shared link keeps them.
const view = computed(() => (route.query.view === 'list' ? 'list' : 'chart'))
const metric = computed(() => (route.query.metric === 'response' ? 'response' : 'uptime'))
const range = computed(() => (RANGES.includes(route.query.range) ? route.query.range : '24h'))
const setQuery = (patch) => router.replace({ query: { ...route.query, ...patch } })

const url = ref(null)
const stats = ref(null)
const checks = ref(null)
const page = ref(1)
const notFound = ref(false)
const error = ref('')
const checking = ref(false)
let timer = null

async function load() {
  try {
    const [u, s] = await Promise.all([api.getUrl(props.id), api.getStats(props.id, range.value)])
    url.value = u
    stats.value = s
    if (view.value === 'list') {
      checks.value = await api.getChecks(props.id, { from: s.from, page: page.value, pageSize: PAGE_SIZE })
    }
    error.value = ''
  } catch (e) {
    if (e.status === 404) notFound.value = true
    else error.value = e.message
  }
}

watch([range, view], () => {
  page.value = 1
  load()
})
watch(page, load)

onMounted(() => {
  load()
  timer = setInterval(load, REFRESH_MS)
})
onUnmounted(() => clearInterval(timer))

const pageCount = computed(() => (checks.value ? Math.max(1, Math.ceil(checks.value.totalCount / PAGE_SIZE)) : 1))

async function checkNow() {
  checking.value = true
  try {
    await api.checkNow(props.id)
    await Promise.all([load(), refreshUrls()])
  } catch (e) {
    showToast('error', 'Check failed', e.message)
  } finally {
    checking.value = false
  }
}

async function remove() {
  try {
    await api.deleteUrl(props.id)
    await refreshUrls()
    showToast('info', 'URL deleted', url.value?.url ?? '')
    router.push('/')
  } catch (e) {
    showToast('error', 'Delete failed', e.message)
  }
}

const stateBadge = computed(() => ({ Up: 'up', Down: 'down', Unknown: 'unknown' })[url.value?.currentState] ?? 'unknown')
</script>

<template>
  <main class="page">
    <RouterLink to="/" class="small">← Dashboard</RouterLink>

    <div v-if="notFound" class="card empty">
      This URL no longer exists. <RouterLink to="/">Back to the dashboard</RouterLink>
    </div>

    <template v-else-if="url">
      <div class="page-header">
        <div class="title">
          <h1 class="break">{{ url.name ?? url.url }}</h1>
          <a :href="url.url" target="_blank" rel="noopener noreferrer" class="small break">
            {{ url.name ? url.url : 'Open in a new tab' }} ↗
          </a>
        </div>
        <div class="header-actions">
          <button :disabled="checking" @click="checkNow">{{ checking ? 'Checking…' : 'Check now' }}</button>
          <ConfirmButton label="Delete URL" confirm-label="Delete for good?" @confirm="remove" />
        </div>
      </div>

      <p v-if="error" class="card error-text">{{ error }}</p>

      <section class="summary">
        <div class="card stat">
          <span class="stat-label">Latest check</span>
          <StatusChip :check="url.latestCheck" />
          <span class="muted small">
            <template v-if="url.latestCheck">
              HTTP {{ statusText(url.latestCheck) }} · {{ formatMs(url.latestCheck.responseTimeMs) }}
            </template>
            <template v-else>Not checked yet</template>
          </span>
          <span v-if="url.latestCheck?.error" class="error-text small break">{{ url.latestCheck.error }}</span>
        </div>
        <div class="card stat">
          <span class="stat-label">Uptime ({{ range }})</span>
          <span class="stat-value">{{ formatPercent(stats?.uptimePercent) }}</span>
          <span class="muted small">{{ stats?.upChecks ?? 0 }} of {{ stats?.totalChecks ?? 0 }} checks up</span>
        </div>
        <div class="card stat">
          <span class="stat-label">Response time ({{ range }})</span>
          <span class="stat-value">{{ formatMs(stats?.avgResponseMs) }}</span>
          <span class="muted small">avg · p95 {{ formatMs(stats?.p95ResponseMs) }}</span>
        </div>
        <div class="card stat">
          <span class="stat-label">Alert state</span>
          <span class="badge" :class="stateBadge">{{ url.currentState }}</span>
          <span class="muted small">Monitoring since {{ formatDateTime(url.createdAt) }}</span>
        </div>
      </section>

      <section class="card">
        <div class="controls">
          <div class="segmented" role="group" aria-label="View">
            <button :class="{ active: view === 'chart' }" @click="setQuery({ view: 'chart' })">Chart</button>
            <button :class="{ active: view === 'list' }" @click="setQuery({ view: 'list' })">List</button>
          </div>
          <div v-if="view === 'chart'" class="segmented" role="group" aria-label="Metric">
            <button :class="{ active: metric === 'uptime' }" @click="setQuery({ metric: 'uptime' })">Uptime</button>
            <button :class="{ active: metric === 'response' }" @click="setQuery({ metric: 'response' })">Response time</button>
          </div>
          <div class="segmented range" role="group" aria-label="Time range">
            <button v-for="r in RANGES" :key="r" :class="{ active: range === r }" @click="setQuery({ range: r })">{{ r }}</button>
          </div>
        </div>

        <template v-if="view === 'chart'">
          <HistoryChart v-if="stats" :stats="stats" :metric="metric" />
          <p class="muted small chart-note">
            <template v-if="stats?.bucketSeconds">Each point is {{ stats.bucketSeconds / 3600 }} hour{{ stats.bucketSeconds > 3600 ? 's' : '' }}. </template>
            <template v-else>Each point is one check. </template>
            Breaks in the line are times the monitor wasn't running.
          </p>
        </template>

        <template v-else-if="checks">
          <HistoryTable :items="checks.items" :gap-threshold-seconds="checks.gapThresholdSeconds" />
          <div v-if="pageCount > 1" class="pager">
            <button class="small" :disabled="page <= 1" @click="page--">← Newer</button>
            <span class="muted small">Page {{ page }} of {{ pageCount }} · {{ checks.totalCount }} checks</span>
            <button class="small" :disabled="page >= pageCount" @click="page++">Older →</button>
          </div>
        </template>
      </section>
    </template>
  </main>
</template>

<style scoped>
.page > a.small { display: inline-block; margin-bottom: 12px; }
.title { min-width: 0; }
.header-actions { display: flex; gap: 8px; }
.summary { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 12px; margin-bottom: 8px; }
.summary .card { margin-bottom: 12px; }
.stat { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; }
.stat-label { font-size: 0.8rem; font-weight: 600; text-transform: uppercase; letter-spacing: 0.03em; color: var(--text-muted); }
.stat-value { font-size: 1.6rem; font-weight: 700; font-variant-numeric: tabular-nums; }
.controls { display: flex; flex-wrap: wrap; gap: 10px; margin-bottom: 16px; }
.range { margin-left: auto; }
.chart-note { margin: 10px 0 0; }
.pager { display: flex; align-items: center; justify-content: center; gap: 12px; margin-top: 12px; }
@media (max-width: 600px) { .range { margin-left: 0; } }
</style>
