/**
 * Global teardown — kills the API and React servers, removes temp database.
 */
import { readFileSync, rmSync, existsSync } from 'node:fs';
import { join } from 'node:path';

const STATE_FILE = join(import.meta.dirname, '.test-state.json');

interface TestState {
  apiPid: number;
  reactPid: number;
  dbPath: string;
  tmpDir: string;
}

function killProcessTree(pid: number): void {
  try {
    // Kill the entire process group (negative pid kills the group)
    process.kill(-pid, 'SIGTERM');
  } catch {
    // Process already exited — that's fine
    try {
      process.kill(pid, 'SIGTERM');
    } catch {
      // truly gone
    }
  }
}

export default async function globalTeardown(): Promise<void> {
  console.log('\n🧹 PrintFarmer UI Validation — Global Teardown');
  console.log('─'.repeat(50));

  if (!existsSync(STATE_FILE)) {
    console.log('  ⚠ No state file found — nothing to tear down');
    return;
  }

  const state: TestState = JSON.parse(readFileSync(STATE_FILE, 'utf-8'));

  // Kill servers
  console.log(`  ▸ Stopping API server (pid ${state.apiPid})...`);
  killProcessTree(state.apiPid);

  console.log(`  ▸ Stopping React dev server (pid ${state.reactPid})...`);
  killProcessTree(state.reactPid);

  // Wait a moment for processes to exit
  await new Promise(r => setTimeout(r, 2000));

  // Remove temp directory
  if (existsSync(state.tmpDir)) {
    console.log(`  ▸ Removing temp dir: ${state.tmpDir}`);
    rmSync(state.tmpDir, { recursive: true, force: true });
  }

  // Remove state file
  rmSync(STATE_FILE, { force: true });

  console.log('─'.repeat(50));
  console.log('  ✅ Teardown complete\n');
}
