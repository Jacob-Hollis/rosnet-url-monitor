import { reactive } from 'vue'

// Pop-up notifications (design.md question 32). Shared by the whole app.
export const toasts = reactive([])

let nextId = 1

/** kind: 'down' | 'up' | 'info' | 'error' */
export function showToast(kind, title, message = '', timeoutMs = 8000) {
  const toast = { id: nextId++, kind, title, message }
  toasts.push(toast)
  if (timeoutMs > 0) setTimeout(() => dismissToast(toast.id), timeoutMs)
}

export function dismissToast(id) {
  const index = toasts.findIndex((t) => t.id === id)
  if (index !== -1) toasts.splice(index, 1)
}
