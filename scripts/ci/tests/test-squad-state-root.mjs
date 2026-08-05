import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import {
  access,
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  rm,
  writeFile,
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..', '..', '..',
);

const requestTimeoutMs = 120_000;

async function exists(filePath) {
  try {
    await access(filePath);
    return true;
  } catch {
    return false;
  }
}

async function loadSquadStateServer() {
  const source = await readFile(path.join(repositoryRoot, '.mcp.json'), 'utf8');
  const config = JSON.parse(source);
  const server = config.mcpServers?.squad_state;

  assert.ok(server, '.mcp.json must define mcpServers.squad_state');
  assert.equal(server.command, 'npx');
  assert.deepEqual(
    server.args,
    ['-y', '@bradygaster/squad-cli@0.11.0', 'state-mcp'],
    'the regression must exercise the repository-pinned Squad CLI integration',
  );

  return server;
}

function startMcpServer(server, cwd) {
  const isWindows = process.platform === 'win32';
  const command = isWindows ? (process.env.ComSpec ?? 'cmd.exe') : server.command;
  const commandArgs = isWindows
    ? ['/d', '/s', '/c', [server.command, ...server.args].join(' ')]
    : server.args;
  const child = spawn(command, commandArgs, {
    cwd,
    detached: !isWindows,
    env: {
      ...process.env,
      ...server.env,
      NO_UPDATE_NOTIFIER: '1',
      SQUAD_NO_PERSONAL: '1',
      npm_config_loglevel: 'error',
      npm_config_update_notifier: 'false',
    },
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });

  child.stdout.setEncoding('utf8');
  child.stderr.setEncoding('utf8');

  let stdoutBuffer = '';
  let stderr = '';
  let nextId = 1;
  const pending = new Map();

  const rejectPending = (error) => {
    for (const { reject, timeout } of pending.values()) {
      clearTimeout(timeout);
      reject(error);
    }
    pending.clear();
  };

  child.stdout.on('data', (chunk) => {
    stdoutBuffer += chunk;
    let newlineIndex = stdoutBuffer.indexOf('\n');
    while (newlineIndex !== -1) {
      const line = stdoutBuffer.slice(0, newlineIndex).replace(/\r$/, '');
      stdoutBuffer = stdoutBuffer.slice(newlineIndex + 1);
      newlineIndex = stdoutBuffer.indexOf('\n');
      if (!line) {
        continue;
      }

      let message;
      try {
        message = JSON.parse(line);
      } catch (error) {
        rejectPending(new Error(
          `Squad state MCP returned invalid JSON: ${line}\n${error}`,
        ));
        continue;
      }

      const request = pending.get(message.id);
      if (request) {
        clearTimeout(request.timeout);
        pending.delete(message.id);
        request.resolve(message);
      }
    }
  });

  child.stderr.on('data', (chunk) => {
    stderr += chunk;
  });

  child.on('error', (error) => {
    rejectPending(error);
  });

  child.on('exit', (code, signal) => {
    rejectPending(new Error(
      `Squad state MCP exited before responding (code=${code}, signal=${signal}).\n${stderr}`,
    ));
  });

  const request = (method, params) => {
    const id = nextId;
    nextId += 1;

    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        pending.delete(id);
        reject(new Error(
          `Timed out waiting for ${method} from Squad state MCP.\n${stderr}`,
        ));
      }, requestTimeoutMs);

      pending.set(id, { resolve, reject, timeout });
      child.stdin.write(`${JSON.stringify({
        jsonrpc: '2.0',
        id,
        method,
        params,
      })}\n`);
    });
  };

  const notify = (method, params) => {
    child.stdin.write(`${JSON.stringify({
      jsonrpc: '2.0',
      method,
      params,
    })}\n`);
  };

  const close = async () => {
    if (child.exitCode !== null || child.signalCode !== null) {
      return;
    }

    const exitPromise = once(child, 'exit');
    child.stdin.end();

    if (isWindows) {
      const script = [
        `$rootId = ${child.pid}`,
        '$processes = @(Get-CimInstance Win32_Process)',
        '$ids = New-Object System.Collections.Generic.List[int]',
        'function Add-Descendants([int]$parentId) {',
        '  foreach ($process in $processes) {',
        '    if ($process.ParentProcessId -eq $parentId) {',
        '      Add-Descendants ([int]$process.ProcessId)',
        '      $ids.Add([int]$process.ProcessId)',
        '    }',
        '  }',
        '}',
        'Add-Descendants $rootId',
        '$ids.Add($rootId)',
        'foreach ($processId in $ids) {',
        '  Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue',
        '}',
        'exit 0',
      ].join('\n');
      const terminator = spawn('powershell.exe', [
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        script,
      ], {
        stdio: ['ignore', 'ignore', 'pipe'],
        windowsHide: true,
      });
      terminator.stderr.setEncoding('utf8');
      let terminationError = '';
      terminator.stderr.on('data', (chunk) => {
        terminationError += chunk;
      });
      const [code] = await once(terminator, 'exit');
      assert.equal(
        code,
        0,
        `failed to terminate the Squad state MCP process tree\n${terminationError}`,
      );
    } else {
      process.kill(-child.pid, 'SIGTERM');
    }

    const exited = await Promise.race([
      exitPromise.then(() => true),
      delay(3_000).then(() => false),
    ]);
    if (!exited && !isWindows) {
      process.kill(-child.pid, 'SIGKILL');
      await exitPromise;
    } else if (!exited) {
      assert.fail('Squad state MCP process tree did not terminate');
    }
  };

  return { request, notify, close };
}

test('Squad state MCP confines decision and state writes to .squad and rejects traversal', {
  timeout: requestTimeoutMs + 10_000,
}, async () => {
  const fixtureRoot = await mkdtemp(path.join(os.tmpdir(), 'printfarmer-squad-state-'));
  const squadDir = path.join(fixtureRoot, '.squad');
  let client;

  try {
    await mkdir(squadDir, { recursive: true });
    await copyFile(
      path.join(repositoryRoot, '.squad', 'config.json'),
      path.join(squadDir, 'config.json'),
    );
    await writeFile(path.join(squadDir, 'team.md'), '## Members\n', 'utf8');

    const server = await loadSquadStateServer();
    client = startMcpServer(server, fixtureRoot);

    const initialize = await client.request('initialize', {
      protocolVersion: '2024-11-05',
      capabilities: {},
      clientInfo: {
        name: 'printfarmer-squad-state-regression',
        version: '1.0.0',
      },
    });
    assert.equal(initialize.error, undefined);
    client.notify('notifications/initialized');

    const decisionFilename = 'parker-issue-1130-decision-regression.md';
    const decisionResponse = await client.request('tools/call', {
      name: 'squad_decide',
      arguments: {
        author: 'parker',
        summary: 'Issue 1130 decision regression',
        body: 'Decision writes must remain under .squad.',
        references: ['#1130'],
      },
    });

    assert.equal(decisionResponse.error, undefined);
    assert.notEqual(decisionResponse.result?.isError, true);
    assert.equal(
      await exists(path.join(fixtureRoot, 'decisions', 'inbox', decisionFilename)),
      false,
      'decision state must not be written at repository root',
    );
    assert.match(
      await readFile(path.join(squadDir, 'decisions', 'inbox', decisionFilename), 'utf8'),
      /\*\*What:\*\* Issue 1130 decision regression/,
    );

    const stateKey = 'decisions/inbox/issue-1130-regression.md';
    const stateContent = 'Issue #1130 state-root regression\n';
    const writeResponse = await client.request('tools/call', {
      name: 'squad_state_write',
      arguments: {
        key: stateKey,
        content: stateContent,
      },
    });

    assert.equal(writeResponse.error, undefined);
    assert.notEqual(writeResponse.result?.isError, true);
    assert.equal(
      await exists(path.join(fixtureRoot, stateKey)),
      false,
      'mutable state must not be written at repository root',
    );
    assert.equal(
      await readFile(path.join(squadDir, stateKey), 'utf8'),
      stateContent,
    );

    const escapeResponse = await client.request('tools/call', {
      name: 'squad_state_write',
      arguments: {
        key: 'decisions/inbox/../../../outside.md',
        content: 'must not escape\n',
      },
    });

    assert.equal(escapeResponse.error, undefined);
    assert.equal(
      escapeResponse.result?.isError,
      true,
      'an escaping state key must be surfaced as an MCP tool error',
    );
    assert.equal(await exists(path.join(fixtureRoot, 'outside.md')), false);
    assert.equal(await exists(path.join(squadDir, 'outside.md')), false);
  } finally {
    await client?.close();
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});
