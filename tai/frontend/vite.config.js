import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// TAI 本地后端（Node.js）默认监听 127.0.0.1:17821，并直接托管 dist 目录。
// 开发模式下通过 Vite 代理 /api，NDJSON 流按原样透传。
const BACKEND = process.env.TAI_BACKEND || 'http://127.0.0.1:17821';

export default defineConfig({
  plugins: [react()],
  base: '/',
  server: {
    host: '127.0.0.1',
    port: 5173,
    proxy: {
      '/api': { target: BACKEND, changeOrigin: true },
    },
  },
  preview: {
    host: '127.0.0.1',
    proxy: {
      '/api': { target: BACKEND, changeOrigin: true },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
  },
});
