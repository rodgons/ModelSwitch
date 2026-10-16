import { fileURLToPath, URL } from 'node:url'
import stylex from '@stylexjs/unplugin'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vitest/config'

// The api the dev server forwards to (dotnet run --project src/ModelSwitch.Api with ASPNETCORE_URLS=https://localhost:5081). The browser
// sees one origin for cookies and antiforgery (ADR-0011); the api must be reached over HTTPS because its __Host- cookies and antiforgery
// require it, so the proxy talks to it with the ASP.NET development certificate.
const api = process.env.MODELSWITCH_API_URL ?? 'https://localhost:5081'

export default defineConfig({
  plugins: [
    stylex.vite({
      useCSSLayers: true,
      // 'off' would also skip compiling in tests; 'full' adds a runtime whose middleware keeps Vitest's servers alive.
      devMode: process.env.VITEST ? 'css-only' : 'full',
      // The compiler resolves imported *.stylex.ts variables itself, so it needs the '@' alias too.
      aliases: { '@/*': [fileURLToPath(new URL('./src/*', import.meta.url))] },
      unstable_moduleResolution: { type: 'commonJS' },
    }),
    vue(),
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    proxy: Object.fromEntries(
      ['/admin', '/v1', '/health', '/ready', '/openapi'].map((path) => [
        path,
        { target: api, changeOrigin: false, secure: false },
      ]),
    ),
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.spec.ts'],
    // The StyleX plugin's dev CSS endpoint keeps Vitest's internal servers open; nothing needs a graceful close.
    teardownTimeout: 1000,
  },
})
