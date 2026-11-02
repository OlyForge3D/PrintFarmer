import { defineConfig, devices } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { isAbsolute, join, resolve } from 'node:path';
import { initializeRun, sourceIdentity } from './e2e/slicer-settings/run-context';

const orcaRoot = process.env.ORCA_TEST_ROOT;
if (!orcaRoot) throw new Error('ORCA_TEST_ROOT must name an isolated upstream OrcaSlicer 2.4.2 portable directory');
const executable = join(orcaRoot, process.platform === 'win32' ? 'orca-slicer.exe' : 'bin/orca-slicer');
if (!existsSync(executable) || !existsSync(join(orcaRoot, 'resources', 'profiles'))) {
  throw new Error('ORCA_TEST_ROOT must contain the real executable and resources/profiles');
}
const parent = process.env.PF_SETTINGS_TEST_ROOT ?? tmpdir();
const context = initializeRun(parent, sourceIdentity(process.env.TEST_WORKER_INDEX === undefined));
const { root } = context;
process.env.API_BASE_URL = 'http://127.0.0.1:15245';
process.env.BASE_URL = 'http://127.0.0.1:13000';
process.env.PFARM__Setup__BootstrapSecret = 'isolated-settings-regression-bootstrap-only';
mkdirSync(root, { recursive: true });
const profiles = join(root, 'worker-profiles');
if (!existsSync(profiles)) {
  mkdirSync(profiles);
  // Only the chosen fixture family and its universal material parents. Neither
  // the installed stock directory nor another worker's cache is ever writable.
  for (const entry of ['BBL', 'BBL.json', 'OrcaFilamentLibrary', 'OrcaFilamentLibrary.json']) {
    cpSync(join(orcaRoot, 'resources', 'profiles', entry), join(profiles, entry), { recursive: true });
  }
}
for (const directory of ['gcode', 'models', 'profiles', 'snapshots', 'thumbnails', 'artifacts', 'jobs', 'custom-profiles', 'cache', 'temp', 'data-protection', 'calibration-blobs']) {
  mkdirSync(join(root, directory), { recursive: true });
}
for (const directory of ['roaming', 'local', 'config', 'cache']) {
  mkdirSync(join(root, 'engine-user', directory), { recursive: true });
}

const common = {
  ASPNETCORE_ENVIRONMENT: 'Development',
  WorkerAuth__SharedKey: 'isolated-settings-regression-only',
  Logging__LogLevel__Default: 'Warning',
  DATAPROTECTION_KEYS_PATH: join(root, 'data-protection'),
  TempStorage__Path: join(root, 'temp'),
  PRINTFARM_TEMP_ROOT: join(root, 'temp'),
  TEMP: join(root, 'temp'),
  TMP: join(root, 'temp'),
  APPDATA: join(root, 'engine-user', 'roaming'),
  LOCALAPPDATA: join(root, 'engine-user', 'local'),
  XDG_CONFIG_HOME: join(root, 'engine-user', 'config'),
  XDG_CACHE_HOME: join(root, 'engine-user', 'cache'),
};
const src = resolve(import.meta.dirname, '..', '..');
const apiEnv = {
  ...common,
  ASPNETCORE_URLS: process.env.API_BASE_URL,
  ASPNETCORE_CONTENTROOT: join(src, 'api'),
  DB_PROVIDER: 'sqlite',
  ConnectionStrings__Default: `Data Source=${join(root, 'app.db')}`,
  ConnectionStrings__SlicerDatabase: `Data Source=${join(root, 'slicer.db')}`,
  PFARM__Setup__BootstrapSecret: process.env.PFARM__Setup__BootstrapSecret,
  NetworkDiscovery__EnableDiscovery: 'false',
  ALLOWED_NETWORK_RANGES: '127.0.0.1/32',
  ALLOWED_ORIGINS: process.env.BASE_URL,
  GCODE_STORAGE_PATH: join(root, 'gcode'),
  MODEL_UPLOAD_PATH: join(root, 'models'),
  SLICER_PROFILES_PATH: join(root, 'profiles'),
  SNAPSHOT_STORAGE_PATH: join(root, 'snapshots'),
  PRINTER_FILE_THUMBNAIL_PATH: join(root, 'thumbnails'),
  ArtifactStorage__RootPath: join(root, 'artifacts'),
  ModelStorage__Path: join(root, 'models'),
  GcodeStorage__Path: join(root, 'gcode'),
  Calibration__BlobStorage__RootPath: join(root, 'calibration-blobs'),
};
const workerEnv = {
  ...common,
  ASPNETCORE_URLS: 'http://127.0.0.1:15246',
  ASPNETCORE_CONTENTROOT: join(src, 'orcaslicer-worker'),
  SlicerApi__BaseUrl: process.env.API_BASE_URL,
  Worker__StorageEndpoint: process.env.API_BASE_URL,
  Worker__WorkingDirectory: join(root, 'jobs'),
  PF_SETTINGS_RUN_ROOT: root,
  Worker__OrcaSlicerPath: executable,
  Worker__EngineVersion: '2.4.2',
  // Upstream portable Orca rejects --version; require the fresh G-code header.
  Worker__VerifyBinaryVersion: 'false',
  SlicerRegistry__Version: '2.4.2',
  SlicerRegistry__Host: 'http://127.0.0.1:15246',
  ORCA_PROFILES_PATH: profiles,
  ORCA_STOCK_PROFILES_PATH: join(orcaRoot, 'resources', 'profiles'),
  ORCA_CUSTOM_PROFILES_PATH: join(root, 'custom-profiles'),
  PROFILE_CACHE_PATH: join(root, 'cache'),
};

if (process.env.TEST_WORKER_INDEX === undefined) {
  // Resolve real .NET configuration/path providers before any application host
  // can write. This also builds the referenced API/worker from this source tree.
  try {
    const cli = {
      DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false',
      DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: 'false',
      DOTNET_CLI_TELEMETRY_OPTOUT: 'true',
      DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: 'true',
      DOTNET_NOLOGO: 'true',
      DOTNET_CLI_UI_LANGUAGE: 'en-US',
    };
    const packageCache = process.env.NUGET_PACKAGES ?? execFileSync('dotnet', ['nuget', 'locals', 'global-packages', '--list'], {
      cwd: src, env: { ...process.env, ...cli }, encoding: 'utf8',
    }).trim().replace(/^global-packages:\s*/, '');
    if (!isAbsolute(packageCache)) throw new Error('Cannot resolve the existing NuGet tool-cache location');
    const output = execFileSync('dotnet', ['run', '--file', join(import.meta.dirname, 'e2e', 'slicer-settings', 'isolation-preflight.cs'), '--', join(src, 'api')], {
      cwd: src,
      env: { ...process.env, ...workerEnv, ...apiEnv, ...cli, NUGET_PACKAGES: packageCache, DOTNET_CLI_HOME: join(root, 'dotnet-cli') },
      encoding: 'utf8',
      timeout: 600_000,
      maxBuffer: 8 * 1024 * 1024,
    });
    writeFileSync(join(root, 'preflight.log'), output);
  } catch (error) {
    const failure = error as Error & { stdout?: string; stderr?: string };
    writeFileSync(join(root, 'preflight.log'), `${failure.stdout ?? ''}\n${failure.stderr ?? ''}\n${failure.message}`);
    throw new Error(`Private configuration preflight failed before host startup; inspect ${join(root, 'preflight.log')}`, { cause: error });
  }
}
const isolation: { runId?: string; hostStarted?: boolean } = JSON.parse(readFileSync(join(root, 'isolation.json'), 'utf8'));
if (isolation.runId !== context.runId || isolation.hostStarted !== false) {
  throw new Error('Missing or mismatched effective-path preflight; refusing host startup');
}
process.stdout.write(`Settings run ${context.runId}, source ${context.source.sourceSha}, evidence ${root}\n`);

export default defineConfig({
  testDir: './e2e/slicer-settings',
  testMatch: '**/*.spec.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 6 * 60 * 60 * 1000,
  expect: { timeout: 15_000 },
  reporter: [['list']],
  outputDir: join(root, 'playwright'),
  use: {
    ...devices['Desktop Chrome'],
    baseURL: process.env.BASE_URL,
    viewport: { width: 1600, height: 1000 },
    actionTimeout: 20_000,
    navigationTimeout: 60_000,
    // Authenticated traces contain tokens; keep evidence to values and G-code.
    trace: 'off',
    video: 'off',
    screenshot: 'only-on-failure',
  },
  webServer: [
    {
      command: `dotnet "${join(src, 'moonraker-emulator', 'Farm.Moonraker.Emulator', 'bin', 'Debug', 'net10.0', 'Farm.Moonraker.Emulator.dll')}"`,
      cwd: root,
      url: 'http://127.0.0.1:17125/healthz',
      reuseExistingServer: false,
      timeout: 60_000,
      env: {
        ...common,
        ASPNETCORE_CONTENTROOT: join(src, 'moonraker-emulator', 'Farm.Moonraker.Emulator'),
        ASPNETCORE_URLS: 'http://127.0.0.1:17125',
        Emulator__Scenario: 'Ready',
        Emulator__EnableControlApi: 'false',
        Emulator__TimeScale: '0',
      },
    },
    {
      command: `dotnet "${join(src, 'api', 'bin', 'Debug', 'net10.0', 'Farm.Web.Api.dll')}"`,
      cwd: root,
      url: `${process.env.API_BASE_URL}/healthz`,
      reuseExistingServer: false,
      timeout: 180_000,
      env: apiEnv,
    },
    {
      command: `dotnet "${join(src, 'orcaslicer-worker', 'bin', 'Debug', 'net10.0', 'Farm.OrcaSlicer.Worker.dll')}"`,
      cwd: root,
      url: 'http://127.0.0.1:15246/health/ready',
      reuseExistingServer: false,
      timeout: 600_000,
      env: workerEnv,
    },
    {
      command: 'npm run dev -- --host 127.0.0.1 --port 13000 --strictPort',
      url: process.env.BASE_URL,
      reuseExistingServer: false,
      timeout: 120_000,
      env: { VITE_API_BASE_URL: process.env.API_BASE_URL },
    },
  ],
});
