// Display helpers. The API sends UTC ISO strings; everything is shown in the browser's local time.

export function formatDateTime(iso) {
  if (!iso) return '—'
  return new Date(iso).toLocaleString(undefined, {
    year: 'numeric', month: 'short', day: 'numeric',
    hour: '2-digit', minute: '2-digit', second: '2-digit',
  })
}

/** Axis label: the date at local midnight (and always for 7d/30d), otherwise the time. */
export function formatTimeShort(ms, rangeName) {
  const d = new Date(ms)
  const midnight = d.getHours() === 0 && d.getMinutes() === 0
  if (rangeName === '7d' || rangeName === '30d' || (midnight && rangeName !== '1h')) {
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
  }
  return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}

const MINUTE = 60_000
const HOUR = 60 * MINUTE
const DAY = 24 * HOUR
const TICK_STEP = { '1h': 10 * MINUTE, '6h': HOUR, '24h': 3 * HOUR, '7d': DAY, '30d': 5 * DAY }

/** Chart axis ticks on round local times (e.g. every 3 hours from midnight) between min and max. */
export function alignedTicks(min, max, rangeName) {
  const step = TICK_STEP[rangeName] ?? 3 * HOUR
  const start = new Date(min)
  start.setHours(0, 0, 0, 0)
  let t = start.getTime()
  while (t < min) t += step
  const ticks = []
  for (; t <= max; t += step) ticks.push({ value: t })
  return ticks
}

export function timeAgo(iso, now = Date.now()) {
  if (!iso) return 'never'
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000))
  if (seconds < 10) return 'just now'
  if (seconds < 60) return `${seconds}s ago`
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  if (hours < 48) return `${hours} h ago`
  return `${Math.round(hours / 24)} days ago`
}

export function formatDuration(ms) {
  const minutes = Math.round(ms / 60000)
  if (minutes < 60) return `${minutes} min`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `${hours} h ${minutes % 60} min`
  return `${Math.floor(hours / 24)} days ${hours % 24} h`
}

export function formatMs(ms) {
  if (ms === null || ms === undefined) return '—'
  return ms >= 1000 ? `${(ms / 1000).toFixed(2)} s` : `${Math.round(ms)} ms`
}

export function formatPercent(p) {
  if (p === null || p === undefined) return '—'
  return `${p.toFixed(p === 100 || p === 0 ? 0 : 1)}%`
}

/** Status text for a check: "200", or the error when there was no response. */
export function statusText(check) {
  if (!check) return '—'
  return check.statusCode ?? 'No response'
}
