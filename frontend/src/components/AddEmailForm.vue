<script setup>
import { ref } from 'vue'
import { api } from '../api/client.js'

const emit = defineEmits(['added'])

const email = ref('')
const error = ref('')
const busy = ref(false)

async function submit() {
  error.value = ''
  busy.value = true
  try {
    const added = await api.addRecipient(email.value)
    email.value = ''
    emit('added', added)
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <form @submit.prevent="submit">
    <div class="form-row">
      <input v-model="email" type="email" placeholder="ops@example.com" aria-label="Email address" required />
      <button class="primary" type="submit" :disabled="busy || !email.trim()">
        {{ busy ? 'Adding…' : 'Add email' }}
      </button>
    </div>
    <p v-if="error" class="error-text">{{ error }}</p>
  </form>
</template>
