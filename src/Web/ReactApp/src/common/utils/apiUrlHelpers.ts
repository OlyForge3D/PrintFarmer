/**
 * Shared API URL and authentication helpers for all frontend services.
 * These helpers ensure consistent API URL resolution and auth header generation
 * across the entire application.
 */

/**
 * Get the API base URL from environment variable or fall back to relative path.
 * Supports both monolithic deployments (/api) and external API servers.
 * 
 * @example
 * // With VITE_API_BASE_URL=http://localhost:5245
 * getApiBaseUrl() // => "http://localhost:5245/api"
 * 
 * // Without environment variable
 * getApiBaseUrl() // => "/api"
 */
export const getApiBaseUrl = (): string => {
  const rawBase = import.meta.env.VITE_API_BASE_URL;
  if (!rawBase || rawBase.trim() === '') return '/api';
  const trimmed = rawBase.replace(/\/$/, '');
  if (/\/(api)(\/|$)/.test(trimmed)) return trimmed;
  return `${trimmed}/api`;
};

/**
 * Get authorization headers with Bearer token from localStorage.
 * Returns empty object if no token is stored.
 * 
 * @example
 * // With token
 * getAuthHeaders() // => { Authorization: "Bearer eyJhbGc..." }
 * 
 * // Without token
 * getAuthHeaders() // => {}
 */
export const getAuthHeaders = (): HeadersInit => {
  const token = localStorage.getItem('auth-token');
  if (!token) return {};
  return { Authorization: `Bearer ${token}` };
};

/**
 * Get SignalR hub URL respecting external API base URL configuration.
 * Used for WebSocket connections to SignalR hubs.
 * 
 * @example
 * // With VITE_API_BASE_URL=http://localhost:5245
 * getHubUrl('/hubs/printers') // => "http://localhost:5245/hubs/printers"
 * 
 * // Without environment variable
 * getHubUrl('/hubs/printers') // => "/hubs/printers"
 */
export const getHubUrl = (hubPath: string): string => {
  const rawBase = import.meta.env.VITE_API_BASE_URL as string | undefined;
  if (!rawBase || rawBase.trim() === '') return hubPath;
  const trimmed = rawBase.replace(/\/$/, '');
  if (/^https?:\/\//.test(trimmed)) {
    // Full URL provided
    return `${trimmed}${hubPath}`;
  }
  // Relative path - just return as-is
  return hubPath;
};
