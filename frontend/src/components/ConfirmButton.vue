<script setup>
import { onUnmounted, ref } from 'vue'

// A delete button that asks for a second click instead of a browser confirm() dialog.
defineProps({
  label: { type: String, default: 'Delete' },
  confirmLabel: { type: String, default: 'Confirm?' },
  disabled: { type: Boolean, default: false },
})
const emit = defineEmits(['confirm'])

const armed = ref(false)
let timer = null

function click() {
  if (armed.value) {
    clearTimeout(timer)
    armed.value = false
    emit('confirm')
    return
  }
  armed.value = true
  timer = setTimeout(() => (armed.value = false), 3000)
}

onUnmounted(() => clearTimeout(timer))
</script>

<template>
  <button type="button" class="small danger" :class="{ confirm: armed }" :disabled="disabled" @click="click">
    {{ armed ? confirmLabel : label }}
  </button>
</template>
