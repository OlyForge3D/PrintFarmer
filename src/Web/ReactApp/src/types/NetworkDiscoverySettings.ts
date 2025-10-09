// Network discovery settings type matching backend NetworkDiscoverySettings
export interface NetworkDiscoverySettings {
  enableDiscovery: boolean;
  discoverySubnets: string[];
  ports: number[];
  clientTimeoutMs?: number;
  requestDelayMs?: number;
  maxConcurrentRequests?: number;
  maxRetries?: number;
  // Add any additional fields from backend as needed
}
