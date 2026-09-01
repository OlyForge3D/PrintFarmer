// Health-check API — plain exported functions sharing the axios instance from
// `httpClient.ts`. Split out of the `ApiClient` monolith (`services/api.ts`) so
// the setup wizard (statically imported by App.tsx) no longer eagerly pulls in
// the whole 486-method class via `common/hooks/useApi.ts`. See issue #2343.
import { client } from "@/services/api/httpClient";
import type { HealthStatus } from "@/types/api";

export async function getHealthStatus(): Promise<HealthStatus> {
  const response = await client.get<HealthStatus>("/health");
  return response.data as HealthStatus;
}

export async function getBasicHealth(): Promise<{ status: string }> {
  const response = await client.get<{ status: string }>("/healthz");
  return response.data;
}
