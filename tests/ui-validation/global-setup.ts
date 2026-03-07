/**
 * Global setup — starts the .NET API server and React dev server.
 *
 * Uses a fresh temporary SQLite database so every run starts clean.
 * Waits for health-check endpoints before handing control to tests.
 */
import { spawn, type ChildProcess } from 'node:child_process';
import { mkdtempSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import type { FullConfig } from '@playwright/test';

const REPO_ROOT = resolve(import.meta.dirname, '../..');
const SRC_DIR = join(REPO_ROOT, 'src');
const REACT_DIR = join(SRC_DIR, 'Web/ReactApp');

const API_PORT = 5245;
const REACT_PORT = 3000;
const API_URL = `http://localhost:${API_PORT}`;
const REACT_URL = `http://localhost:${REACT_PORT}`;
const HEALTH_URL = `${API_URL}/healthz`;

const MAX_API_WAIT_MS = 180_000;
const MAX_REACT_WAIT_MS = 60_000;
const POLL_INTERVAL_MS = 1_500;

// Stash for teardown
const STATE_FILE = join(import.meta.dirname, '.test-state.json');

interface TestState {
  apiPid: number;
  reactPid: number;
  dbPath: string;
  tmpDir: string;
}

async function waitForUrl(url: string, timeoutMs: number, label: string): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(url);
      if (res.ok) {
        console.log(`  ✓ ${label} is ready (${url})`);
        return;
      }
    } catch {
      // not up yet
    }
    await new Promise(r => setTimeout(r, POLL_INTERVAL_MS));
  }
  throw new Error(`Timed out waiting for ${label} at ${url} after ${timeoutMs / 1000}s`);
}

function spawnDetached(
  command: string,
  args: string[],
  options: { cwd: string; env?: NodeJS.ProcessEnv },
): ChildProcess {
  const child = spawn(command, args, {
    ...options,
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: true,
    env: { ...process.env, ...options.env },
  });

  // Pipe output so failures are visible
  child.stdout?.on('data', (data: Buffer) => {
    const line = data.toString().trim();
    if (line) console.log(`  [${command}] ${line}`);
  });
  child.stderr?.on('data', (data: Buffer) => {
    const line = data.toString().trim();
    if (line) console.error(`  [${command}:err] ${line}`);
  });

  return child;
}

export default async function globalSetup(_config: FullConfig): Promise<void> {
  console.log('\n🚀 PrintFarmer UI Validation — Global Setup');
  console.log('─'.repeat(50));

  // 1. Create a temp directory + fresh SQLite database path
  const tmpDir = mkdtempSync(join(tmpdir(), 'pf-ui-test-'));
  const dbPath = join(tmpDir, 'test-farm.db');
  console.log(`  📂 Temp dir: ${tmpDir}`);
  console.log(`  🗄  DB path: ${dbPath}`);

  // 2. Start the .NET API server
  console.log('  ▸ Starting .NET API server...');
  const apiProcess = spawnDetached('dotnet', ['run', '--project', './api/Farm.Web.Api.csproj', '--no-launch-profile'], {
    cwd: SRC_DIR,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
      DB_PROVIDER: 'sqlite',
      ConnectionStrings__Default: `Data Source=${dbPath}`,
      // Disable network discovery to avoid hitting the real network
      NetworkDiscovery__EnableDiscovery: 'false',
    },
  });

  if (!apiProcess.pid) {
    throw new Error('Failed to spawn .NET API process');
  }

  // 3. Wait for API health check
  await waitForUrl(HEALTH_URL, MAX_API_WAIT_MS, '.NET API');

  // 4. Start the React dev server
  console.log('  ▸ Starting React dev server...');
  const reactProcess = spawnDetached('npx', ['vite', '--port', String(REACT_PORT), '--strictPort'], {
    cwd: REACT_DIR,
    env: {
      NODE_ENV: 'development',
      BROWSER: 'none', // don't open browser
    },
  });

  if (!reactProcess.pid) {
    throw new Error('Failed to spawn React dev server process');
  }

  // 5. Wait for React dev server
  await waitForUrl(REACT_URL, MAX_REACT_WAIT_MS, 'React dev server');

  // 6. Persist PIDs for teardown
  const state: TestState = {
    apiPid: apiProcess.pid,
    reactPid: reactProcess.pid,
    dbPath,
    tmpDir,
  };
  writeFileSync(STATE_FILE, JSON.stringify(state, null, 2));

  // Detach so the parent can exit without killing children
  apiProcess.unref();
  reactProcess.unref();

  console.log('─'.repeat(50));
  console.log('  ✅ Setup complete — running tests\n');
}
