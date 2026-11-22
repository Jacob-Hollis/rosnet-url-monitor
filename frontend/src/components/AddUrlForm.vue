<script setup>
import { ref } from 'vue'
import { api } from '../api/client.js'

const emit = defineEmits(['added'])

const url = ref('')
const name = ref('')
const error = ref('')
const busy = ref(false)

async function submit() {
  error.value = ''
  busy.value = true
  try {
    const added = await api.addUrl(url.value, name.value)
    url.value = ''
    name.value = ''
    emit('added', added)
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <form class="card" @submit.prevent="submit">
    <h2>Add a URL</h2>
    <div class="form-row">
      <input v-model="url" type="url" placeholder="https://example.com/health" aria-label="URL" required />
      <input v-model="name" type="text" placeholder="Name (optional)" aria-label="Name" class="name" />
      <button class="primary" type="submit" :disabled="busy || !url.trim()">
        {{ busy ? 'Adding…' : 'Add URL' }}
      </button>
    </div>
    <p v-if="error" class="error-text">{{ error }}</p>
  </form>
</template>

<style scoped>
.name { flex: 0 1 200px !important; }
</style>
