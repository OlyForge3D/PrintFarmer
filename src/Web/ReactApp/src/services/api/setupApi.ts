// Setup/Spoolman-bootstrap API — plain exported functions sharing the axios
// instance from `httpClient.ts`. Split out of the `ApiClient` monolith
// (`services/api.ts`) so the first-run setup wizard, statically imported by
// App.tsx, no longer eagerly pulls in the whole 486-method class.
// See issue #2343.
import { client } from "@/services/api/httpClient";
import type { SetupBootstrapResponse, SpoolmanDiscoveryResult } from "@/types/api";

/**
 * Get setup status
 */
export async function getSetupStatus(): Promise<Record<string, unknown>> {
  const response = await client.get('/setup/status');
  return response.data;
}

/**
 * Get non-secret deployment defaults while first-run setup is required.
 */
export async function getSetupBootstrap(signal?: AbortSignal): Promise<SetupBootstrapResponse> {
  const response = await client.get<SetupBootstrapResponse>(
    '/setup/bootstrap',
    { signal },
  );
  return response.data;
}

/**
 * Create initial admin account
 */
export async function createInitialAdmin(adminData: Record<string, unknown>): Promise<Record<string, unknown>> {
  const response = await client.post('/setup/initial-admin', adminData);
  return response.data;
}

/**
 * Test Spoolman connection
 */
export async function testSpoolmanConnection(baseUrl: string): Promise<Record<string, unknown>> {
  const response = await client.post('/spoolman/test', { baseUrl });
  return response.data;
}

/**
 * Save Spoolman configuration
 */
export async function saveSpoolmanConfig(config: Record<string, unknown>): Promise<Record<string, unknown>> {
  const response = await client.post('/spoolman/config', config);
  return response.data;
}

/**
 * Scan the configured local network ranges for reachable Spoolman instances.
 */
export async function scanNetworkForSpoolman(): Promise<SpoolmanDiscoveryResult[]> {
  const response = await client.post<SpoolmanDiscoveryResult[]>(
    "/spoolman/scan-network"
  );
  return response.data;
}
