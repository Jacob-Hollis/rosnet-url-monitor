import { reactive } from 'vue'
import { api } from '../api/client.js'
import { showToast } from './toasts.js'

// The URL list with latest status, polled for the whole app (not just the dashboard),
// so state-change toasts appear on any page while the UI is open.

export const POLL_INTERVAL_MS = 30_000

export const urlStatus = reactive({
  urls: [],
  loaded: false,
  error: null,
  lastUpdated: null,
})

let previousStates = null // id -> currentState from the previous poll
let timer = null

export async function refreshUrls() {
  try {
    const urls = await api.listUrls()
    notifyStateChanges(urls)
    urlStatus.urls = urls
    urlStatus.error = null
    urlStatus.lastUpdated = new Date()
  } catch (e) {
    urlStatus.error = e.message
  } finally {
    urlStatus.loaded = true
  }
}

/** Toast when a URL's alert state flips Up -> Down or Down -> Up since the last poll. */
function notifyStateChanges(urls) {
  const states = new Map(urls.map((u) => [u.id, u.currentState]))
  if (previousStates) {
    for (const url of urls) {
      const before = previousStates.get(url.id)
      const label = url.name ?? url.url
      if (before === 'Up' && url.currentState === 'Down') {
        showToast('down', `${label} is DOWN`, url.latestCheck?.error ?? '')
      } else if (before === 'Down' && url.currentState === 'Up') {
        showToast('up', `${label} recovered`, 'It is up again.')
      }
    }
  }
  previousStates = states
}

export function startPolling() {
  if (timer) return
  refreshUrls()
  timer = setInterval(refreshUrls, POLL_INTERVAL_MS)
}

export function stopPolling() {
  clearInterval(timer)
  timer = null
}
