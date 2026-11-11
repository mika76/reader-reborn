import { defineConfig } from '@playwright/test'
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

// The smoke test runs the built frontend (npm run build first) against the real server, started on its own
// port with an empty database in a temporary folder.
const port = 5299
const data = mkdtempSync(join(tmpdir(), 'reader-e2e-'))

export default defineConfig({
  testDir: 'e2e',
  timeout: 30_000,
  use: { baseURL: `http://127.0.0.1:${port}` },
  webServer: {
    command: `dotnet run ${resolve('../server/reader.cs')}`,
    url: `http://127.0.0.1:${port}/api/status`,
    timeout: 120_000,
    reuseExistingServer: false,
    env: {
      READER_DB: join(data, 'reader.db'),
      READER_HOST: '127.0.0.1',
      READER_PORT: String(port),
      READER_WEB: resolve('dist'),
      READER_IMAGES: 'off',
    },
  },
})
