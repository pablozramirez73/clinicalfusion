/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build nella cartella wwwroot del server: in produzione la dashboard è servita da ASP.NET Core sullo
// stesso dominio delle API (cookie SameSite=Strict, nessun CORS). In sviluppo Vite inoltra /api al server.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../ClinicalSensorFusion.Server/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
    rollupOptions: {
      output: {
        // Three.js e l'ecosistema R3F in un chunk separato: cambiano raramente e restano in cache.
        manualChunks: (id) => (/node_modules\/(three|@react-three|postprocessing)/.test(id) ? 'three' : undefined),
      },
    },
  },
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5180', '/health': 'http://localhost:5180', '/fhir': 'http://localhost:5180' },
  },
  test: { environment: 'node' },
});
