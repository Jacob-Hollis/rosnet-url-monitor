<script setup>
import { computed } from 'vue'
import { Line } from 'vue-chartjs'
import {
  Chart as ChartJS, Filler, LinearScale, LineElement, PointElement, Tooltip,
} from 'chart.js'
import { alignedTicks, formatDateTime, formatMs, formatTimeShort } from '../utils/format.js'

ChartJS.register(LineElement, PointElement, LinearScale, Tooltip, Filler)

const props = defineProps({
  stats: { type: Object, required: true },
  metric: { type: String, default: 'uptime' }, // 'uptime' | 'response'
})

const css = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim()

const bucketed = computed(() => props.stats.bucketSeconds !== null)

// Gap points (checks === 0) have y = null, so the line breaks where the monitor wasn't running.
const points = computed(() =>
  props.stats.series.map((p) => ({
    x: Date.parse(p.time),
    y: props.metric === 'uptime' ? p.uptimePercent : p.avgResponseMs,
    point: p,
  })),
)

const hasData = computed(() => points.value.some((p) => p.y !== null))

const data = computed(() => {
  const up = css('--up')
  const down = css('--down')
  const accent = css('--accent')
  const isUptime = props.metric === 'uptime'

  return {
    datasets: [
      {
        data: points.value,
        parsing: false,
        spanGaps: false,
        borderWidth: 2,
        // Uptime per check is 100 or 0, so draw it as steps; buckets and response times as lines.
        stepped: isUptime && !bucketed.value ? 'before' : false,
        tension: 0,
        borderColor: isUptime ? up : accent,
        // Colour a segment red when it drops below 100% uptime.
        segment: isUptime ? { borderColor: (ctx) => (ctx.p1.parsed.y < 100 ? down : up) } : {},
        pointRadius: points.value.length > 150 ? 0 : points.value.length < 10 ? 4 : 2.5,
        pointHoverRadius: 5,
        pointBackgroundColor: (ctx) => {
          const y = ctx.raw?.y
          if (!isUptime) return accent
          return y === null || y === undefined || y >= 100 ? up : down
        },
        pointBorderWidth: 0,
        fill: isUptime ? false : { target: 'origin', above: accent + '22' },
      },
    ],
  }
})

const options = computed(() => {
  const muted = css('--text-muted')
  const grid = css('--border')
  const isUptime = props.metric === 'uptime'

  return {
    responsive: true,
    maintainAspectRatio: false,
    animation: false,
    // Points at the very edge of the range (e.g. a check made seconds ago) shouldn't be clipped.
    layout: { padding: { right: 8, top: 8 } },
    clip: false,
    interaction: { mode: 'nearest', axis: 'x', intersect: false },
    scales: {
      x: {
        type: 'linear',
        min: Date.parse(props.stats.from),
        max: Date.parse(props.stats.to),
        afterBuildTicks: (axis) => { axis.ticks = alignedTicks(axis.min, axis.max, props.stats.range) },
        ticks: { color: muted, autoSkip: true, maxRotation: 0, callback: (v) => formatTimeShort(v, props.stats.range) },
        grid: { color: grid },
      },
      y: {
        beginAtZero: true,
        ...(isUptime ? { min: 0, max: 100 } : {}),
        ticks: {
          color: muted,
          callback: (v) => (isUptime ? `${v}%` : formatMs(v)),
        },
        grid: { color: grid },
      },
    },
    plugins: {
      legend: { display: false },
      tooltip: {
        filter: (item) => item.raw.y !== null,
        callbacks: {
          title: (items) => formatDateTime(new Date(items[0].raw.x).toISOString()),
          label: (item) => tooltipLabel(item.raw.point),
        },
      },
    },
  }
})

function tooltipLabel(p) {
  if (bucketed.value) {
    return props.metric === 'uptime'
      ? `Uptime ${p.uptimePercent}% (${p.checks} checks)`
      : `Avg ${formatMs(p.avgResponseMs)} (${p.checks} checks)`
  }
  if (props.metric === 'uptime') {
    return p.uptimePercent >= 100 ? `Up (HTTP ${p.statusCode})` : `Down (${p.statusCode ? 'HTTP ' + p.statusCode : 'no response'})`
  }
  return p.avgResponseMs === null ? 'No response' : formatMs(p.avgResponseMs)
}
</script>

<template>
  <div class="chart-box">
    <Line v-if="hasData" :data="data" :options="options" />
    <p v-else class="empty">No checks in this time range yet.</p>
  </div>
</template>

<style scoped>
.chart-box { position: relative; height: 320px; }
.empty { position: absolute; inset: 0; display: grid; place-items: center; margin: 0; }
</style>
