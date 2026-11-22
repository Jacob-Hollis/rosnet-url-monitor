<script setup>
import { computed } from 'vue'
import StatusChip from './StatusChip.vue'
import { formatDateTime, formatDuration, formatMs, statusText } from '../utils/format.js'

// Check history, newest first, with "No data" rows where the monitor wasn't running (design.md question 10).
const props = defineProps({
  items: { type: Array, required: true },
  gapThresholdSeconds: { type: Number, required: true },
})

const rows = computed(() => {
  const result = []
  props.items.forEach((check, i) => {
    const older = props.items[i + 1]
    result.push({ type: 'check', key: `c${check.id}`, check })
    if (older) {
      const gapMs = Date.parse(check.checkedAt) - Date.parse(older.checkedAt)
      if (gapMs > props.gapThresholdSeconds * 1000) {
        result.push({ type: 'gap', key: `g${check.id}`, from: older.checkedAt, to: check.checkedAt, gapMs })
      }
    }
  })
  return result
})
</script>

<template>
  <div class="table-wrap">
    <table>
      <thead>
        <tr>
          <th>Checked at</th>
          <th>Status</th>
          <th>HTTP</th>
          <th class="num">Response time</th>
          <th>Error</th>
        </tr>
      </thead>
      <tbody>
        <template v-for="row in rows" :key="row.key">
          <tr v-if="row.type === 'check'">
            <td class="nowrap">{{ formatDateTime(row.check.checkedAt) }}</td>
            <td><StatusChip :check="row.check" /></td>
            <td class="mono">{{ statusText(row.check) }}</td>
            <td class="num">{{ formatMs(row.check.responseTimeMs) }}</td>
            <td class="small break">{{ row.check.error ?? '' }}</td>
          </tr>
          <tr v-else class="gap-row">
            <td colspan="5">
              No data for {{ formatDuration(row.gapMs) }}: monitor not running
              ({{ formatDateTime(row.from) }} – {{ formatDateTime(row.to) }})
            </td>
          </tr>
        </template>
        <tr v-if="rows.length === 0">
          <td colspan="5" class="empty">No checks in this time range yet.</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
