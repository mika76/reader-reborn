import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// In development the API is served by ../server/reader.cs on port 5199.
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5199' },
  },
})
