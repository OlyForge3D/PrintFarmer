import { createServer } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../..', import.meta.url));
const server = await createServer({
  configFile: false,
  envDir: false,
  envPrefix: 'PRINTFARMER_MARKETING_UNUSED_',
  root,
  plugins: [react(), {
    name: 'capture-no-backend',
    configureServer(vite) {
      vite.middlewares.use((request, response, next) => {
        if (/^\/(api|hubs)(\/|$)/.test(request.url)) {
          response.writeHead(503, { 'Content-Type': 'text/plain' });
          response.end('Capture server has no backend. Add an explicit browser fixture.');
          return;
        }
        next();
      });
    },
  }],
  resolve: { alias: { '@': fileURLToPath(new URL('../../src', import.meta.url)) } },
  define: {
    __BUILD_TIME__: JSON.stringify('2026-10-30T14:00:00Z'),
    __GIT_HASH__: JSON.stringify('marketing-fixture'),
    __RELEASE_IDENTITY__: 'null',
  },
  server: {
    host: '127.0.0.1',
    port: 4178,
    strictPort: true,
    hmr: false,
    proxy: {},
  },
});
await server.listen();
console.log('Isolated frontend capture server: http://127.0.0.1:4178');
