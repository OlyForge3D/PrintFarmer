import { defineConfig, devices } from '@playwright/test';
import { cpSync, existsSync, mkdirSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const orcaRoot = process.env.ORCA_TEST_ROOT;
if (!orcaRoot) throw new Error('ORCA_TEST_ROOT must name an isolated upstream OrcaSlicer 2.4.2 portable directory');
const executable = join(orcaRoot, process.platform === 'win32' ? 'orca-slicer.exe' : 'bin/orca-slicer');
if (!existsSync(executable) || !existsSync(join(orcaRoot, 'resources', 'profiles'))) {
  throw new Error('ORCA_TEST_ROOT must contain the real executable and resources/profiles');
}
const parent = process.env.PF_SETTINGS_TEST_ROOT ?? tmpdir();
mkdirSync(parent, { recursive: true });
const root = process.env.PF_SETTINGS_RUN_ROOT ?? mkdtempSync(join(parent, 'pf-settings-e2e-'));
process.env.PF_SETTINGS_RUN_ROOT = root;
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
for (const directory of ['gcode', 'models', 'profiles', 'snapshots', 'thumbnails', 'artifacts', 'jobs', 'custom-profiles', 'cache']) {
  mkdirSync(join(root, directory), { recursive: true });
}
for (const directory of ['roaming', 'local', 'config', 'cache']) {
  mkdirSync(join(root, 'engine-user', directory), { recursive: true });
}

const common = {
  ASPNETCORE_ENVIRONMENT: 'Development',
  WorkerAuth__SharedKey: 'isolated-settings-regression-only',
  Logging__LogLevel__Default: 'Warning',
};
const src = resolve(import.meta.dirname, '..', '..');

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
      command: `dotnet run --project "${join('moonraker-emulator', 'Farm.Moonraker.Emulator', 'Farm.Moonraker.Emulator.csproj')}" -c Debug --no-build --no-launch-profile`,
      cwd: src,
      url: 'http://127.0.0.1:17125/healthz',
      reuseExistingServer: false,
      timeout: 60_000,
      env: {
        ...common,
        ASPNETCORE_URLS: 'http://127.0.0.1:17125',
        Emulator__Scenario: 'Ready',
        Emulator__EnableControlApi: 'false',
        Emulator__TimeScale: '0',
      },
    },
    {
      command: `dotnet run --project "${join('api', 'Farm.Web.Api.csproj')}" -c Debug --no-build --no-launch-profile`,
      cwd: src,
      url: `${process.env.API_BASE_URL}/healthz`,
      reuseExistingServer: false,
      timeout: 180_000,
      env: {
        ...common,
        ASPNETCORE_URLS: process.env.API_BASE_URL,
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
      },
    },
    {
      command: `dotnet run --project "${join('orcaslicer-worker', 'Farm.OrcaSlicer.Worker.csproj')}" -c Debug --no-build --no-launch-profile`,
      cwd: src,
      url: 'http://127.0.0.1:15246/health/ready',
      reuseExistingServer: false,
      timeout: 600_000,
      env: {
        ...common,
        ASPNETCORE_URLS: 'http://127.0.0.1:15246',
        SlicerApi__BaseUrl: process.env.API_BASE_URL,
        Worker__StorageEndpoint: process.env.API_BASE_URL,
        Worker__WorkingDirectory: join(root, 'jobs'),
        PF_SETTINGS_RUN_ROOT: root,
        Worker__OrcaSlicerPath: executable,
        Worker__EngineVersion: '2.4.2',
        // Upstream portable Orca rejects --version. The test checks the actual
        // freshly generated G-code header instead of trusting registration.
        Worker__VerifyBinaryVersion: 'false',
        SlicerRegistry__Version: '2.4.2',
        SlicerRegistry__Host: 'http://127.0.0.1:15246',
        ORCA_PROFILES_PATH: profiles,
        ORCA_STOCK_PROFILES_PATH: join(orcaRoot, 'resources', 'profiles'),
        ORCA_CUSTOM_PROFILES_PATH: join(root, 'custom-profiles'),
        PROFILE_CACHE_PATH: join(root, 'cache'),
        APPDATA: join(root, 'engine-user', 'roaming'),
        LOCALAPPDATA: join(root, 'engine-user', 'local'),
        XDG_CONFIG_HOME: join(root, 'engine-user', 'config'),
        XDG_CACHE_HOME: join(root, 'engine-user', 'cache'),
      },
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
