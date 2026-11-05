import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `npm run build` writes into the API's wwwroot, so ASP.NET Core serves the UI and the API from one place.
// `npm run dev` serves the UI with hot reload and forwards /api to the running API.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/DataLens.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: { '/api': 'http://localhost:5080' },
  },
});
