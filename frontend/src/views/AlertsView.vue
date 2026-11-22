<script setup>
import { onMounted, onUnmounted, ref } from 'vue'
import AddEmailForm from '../components/AddEmailForm.vue'
import ConfirmButton from '../components/ConfirmButton.vue'
import { api } from '../api/client.js'
import { showToast } from '../stores/toasts.js'
import { formatDateTime } from '../utils/format.js'

const recipients = ref([])
const events = ref([])
const loaded = ref(false)
const error = ref('')
const testing = ref(false)
const testResult = ref(null) // { ok, message }
let timer = null

async function loadRecipients() {
  recipients.value = await api.listRecipients()
}

async function loadEvents() {
  events.value = await api.listAlertEvents(50)
}

async function loadAll() {
  try {
    await Promise.all([loadRecipients(), loadEvents()])
    error.value = ''
  } catch (e) {
    error.value = e.message
  } finally {
    loaded.value = true
  }
}

onMounted(() => {
  loadAll()
  timer = setInterval(() => loadEvents().catch(() => {}), 30_000)
})
onUnmounted(() => clearInterval(timer))

async function removeRecipient(r) {
  try {
    await api.deleteRecipient(r.id)
    await loadRecipients()
  } catch (e) {
    showToast('error', 'Could not remove email', e.message)
  }
}

async function sendTest() {
  testing.value = true
  testResult.value = null
  try {
    const result = await api.sendTestEmail()
    testResult.value = { ok: true, message: `Test email sent to ${result.recipientCount} address${result.recipientCount === 1 ? '' : 'es'}.` }
  } catch (e) {
    testResult.value = { ok: false, message: e.message }
  } finally {
    testing.value = false
  }
}

const deliveryBadge = {
  Sent: 'up',
  Failed: 'down',
  Suppressed: 'unknown',
  NoRecipients: 'warn',
  Pending: 'unknown',
}
const deliveryLabel = {
  Sent: 'Emailed',
  Failed: 'Email failed',
  Suppressed: 'Not emailed (cooldown)',
  NoRecipients: 'Not emailed (no recipients)',
  Pending: 'Sending…',
}
</script>

<template>
  <main class="page">
    <div class="page-header">
      <div>
        <h1>Alerts</h1>
        <p class="muted small">
          Everyone on this list is emailed when any URL goes down (2 failed checks in a row) or recovers.
          At most one email per URL every 30 minutes.
        </p>
      </div>
    </div>

    <p v-if="error" class="card error-text">{{ error }}</p>

    <section class="card">
      <h2>Alert emails</h2>
      <AddEmailForm @added="loadRecipients" />

      <ul v-if="recipients.length" class="recipients">
        <li v-for="r in recipients" :key="r.id">
          <span class="break">{{ r.email }}</span>
          <span class="muted small nowrap">added {{ formatDateTime(r.createdAt) }}</span>
          <ConfirmButton label="Remove" confirm-label="Remove?" @confirm="removeRecipient(r)" />
        </li>
      </ul>
      <p v-else-if="loaded" class="muted small">No emails yet. Alerts are still recorded below, but nobody is emailed.</p>

      <div class="test-row">
        <button :disabled="testing || recipients.length === 0" @click="sendTest">
          {{ testing ? 'Sending…' : 'Send test email' }}
        </button>
        <span v-if="testResult" :class="testResult.ok ? 'success-text' : 'error-text'">{{ testResult.message }}</span>
      </div>
      <p class="muted small">Running locally, emails go to smtp4dev: open <a href="http://localhost:5050" target="_blank" rel="noopener">http://localhost:5050</a> to read them.</p>
    </section>

    <section class="card table-card">
      <h2 class="table-title">Recent alerts</h2>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Time</th>
              <th>URL</th>
              <th>Event</th>
              <th>Email</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="e in events" :key="e.id">
              <td class="nowrap">{{ formatDateTime(e.occurredAt) }}</td>
              <td class="break"><RouterLink :to="`/urls/${e.monitoredUrlId}`">{{ e.urlName ?? e.url }}</RouterLink></td>
              <td>
                <span class="badge" :class="e.kind === 'Down' ? 'down' : 'up'">{{ e.kind === 'Down' ? 'Went down' : 'Recovered' }}</span>
              </td>
              <td>
                <span class="badge" :class="deliveryBadge[e.delivery]">{{ deliveryLabel[e.delivery] ?? e.delivery }}</span>
                <div v-if="e.error" class="error-text small break">{{ e.error }}</div>
              </td>
            </tr>
            <tr v-if="loaded && events.length === 0">
              <td colspan="4" class="empty">No alerts yet.</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </main>
</template>

<style scoped>
.recipients { list-style: none; padding: 0; margin: 16px 0 0; border-top: 1px solid var(--border); }
.recipients li {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 0;
  border-bottom: 1px solid var(--border);
}
.recipients li > span:first-child { flex: 1; min-width: 0; }
.test-row { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; margin-top: 16px; }
.test-row .success-text, .test-row .error-text { margin: 0; }
.table-card { padding: 0; }
.table-title { padding: 16px 16px 0; }
</style>
