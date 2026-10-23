import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// In development the API runs separately on :5080; in production it serves this build from wwwroot.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5080' },
  },
  build: {
    outDir: '../server/CameraViewer.Api/wwwroot',
    emptyOutDir: true,
    // hls.js alone is ~400 kB; one chunk is fine for a LAN app.
    chunkSizeWarningLimit: 1000,
  },
});
