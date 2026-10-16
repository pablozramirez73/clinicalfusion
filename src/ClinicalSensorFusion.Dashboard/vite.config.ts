/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build nella cartella wwwroot del server: in produzione la dashboard è servita da ASP.NET Core sullo
// stesso dominio delle API (cookie SameSite=Strict, nessun CORS). In sviluppo Vite inoltra /api al server.
// Senza @types/node: basta l'ambiente del processo.
declare const process: { env: Record<string, string | undefined> };
const serverUrl = process.env.SERVER_URL || 'http://localhost:5180';

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
    // Sotto Aspire (src/ClinicalSensorFusion.AppHost) porta e indirizzo del server arrivano dall'orchestratore.
    port: Number(process.env.PORT) || 5173,
    proxy: { '/api': serverUrl, '/health': serverUrl, '/fhir': serverUrl },
  },
  test: { environment: 'node' },
});
