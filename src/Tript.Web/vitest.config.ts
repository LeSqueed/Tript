/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Single configuration used by `vite dev`, `vite build` and `vitest`.
//
// The dev/preview server runs on 2883, NOT on the app host's 2882 (src/Tript.App/LocalPorts.cs):
// both bind the same loopback port, so on 2882 only one of them could ever be up — and the whole
// point of the dev server is to have it running against the real backend.
//
// What `vite dev` is good for is the UI in isolation. It cannot talk to a running backend: the host
// mints a launch key per start and serves the SPA only to a request carrying `?k=`, which vite
// cannot mint, so the dev server shows the "missing key" notice rather than connecting. To exercise
// the real app, build and open the URL the host prints on its READY line.
export default defineConfig({
  plugins: [react()],
  base: './',
  server: {
    port: 2883,
  },
  preview: {
    port: 2883,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    css: false,
    include: ['src/**/*.test.{ts,tsx}'],
    setupFiles: ['src/test/setup.ts'],
  },
});
