import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../../../../site', import.meta.url));
const prefix = '/PrintFarmer/';
const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png' };

createServer(async (request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1:4179');
  if (url.pathname === '/' || url.pathname === '/PrintFarmer') {
    response.writeHead(302, { Location: prefix });
    response.end();
    return;
  }
  if (!url.pathname.startsWith(prefix) || request.method !== 'GET') {
    response.writeHead(404);
    response.end('Not found');
    return;
  }
  let pathname;
  try { pathname = decodeURIComponent(url.pathname.slice(prefix.length)); }
  catch { response.writeHead(400); response.end('Invalid URL encoding'); return; }
  const path = resolve(root, pathname.endsWith('/') || !pathname ? `${pathname}index.html` : pathname);
  const relativePath = relative(root, path);
  if (isAbsolute(relativePath) || relativePath.startsWith(`..${sep}`) || relativePath === '..' || resolve(path) === root) {
    response.writeHead(403);
    response.end('Path outside site');
    return;
  }
  try {
    const body = await readFile(path);
    response.writeHead(200, {
      'Content-Type': types[extname(path)] ?? 'application/octet-stream',
      'Content-Security-Policy': "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'none'; connect-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'",
      'X-Content-Type-Options': 'nosniff',
    });
    response.end(body);
  } catch (error) {
    if (error.code !== 'ENOENT' && error.code !== 'EISDIR') {
      console.error('Site preview failed:', error);
      response.writeHead(500);
      response.end('Preview failed; see server log');
      return;
    }
    response.writeHead(404);
    response.end('Not found');
  }
}).listen(4179, '127.0.0.1', () => {
  console.log('Marketing preview: http://127.0.0.1:4179/PrintFarmer/');
});
