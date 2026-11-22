import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

// The API runs on http://localhost:5265 (backend launchSettings "http" profile).
// Proxying /api means the browser only talks to Vite, so no CORS setup is needed.
const apiTarget = process.env.API_URL ?? 'http://localhost:5265'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true },
    },
  },
})
