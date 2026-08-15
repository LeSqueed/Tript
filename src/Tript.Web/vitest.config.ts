/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Single configuration used by `vite dev`, `vite build` and `vitest`.
// The frontend is served from embedded resources in release; `vite preview` (http://localhost:2882)
// is the dev host. The control socket and content server are addressed by the IPC client at runtime.
export default defineConfig({
  plugins: [react()],
  base: './',
  server: {
    port: 2882,
  },
  preview: {
    port: 2882,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    css: false,
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
