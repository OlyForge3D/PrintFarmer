// Network discovery settings type matching backend NetworkDiscoverySettings
export interface NetworkDiscoverySettings {
  enableDiscovery: boolean;
  discoverySubnets: string[];
  ports: number[];
  clientTimeoutMs?: number;
  requestDelayMs?: number;
  maxConcurrentRequests?: number;
  maxRetries?: number;
  lastHeartbeat?: string; // ISO 8601 timestamp of last heartbeat from discovery service
}
