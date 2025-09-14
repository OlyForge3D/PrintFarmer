// Simple validation utilities shared between SetupWizard and SettingsPage
// Keep lightweight to avoid pulling external libs.

export function isValidCidr(input: string): boolean {
  if (!input) return false;
  const match = input.match(/^([0-9]{1,3})(?:\.([0-9]{1,3})){3}\/(\d{1,2})$/);
  if (!match) return false;
  const parts = input.split('/');
  const ip = parts[0].split('.').map(n => Number(n));
  const prefix = Number(parts[1]);
  if (prefix < 0 || prefix > 32) return false;
  return ip.every(oct => oct >= 0 && oct <= 255);
}

export function normalizeUrl(url: string): string {
  // Generic lightweight normalization (trim + single trailing slash removal)
  return url.trim().replace(/\/$/, '');
}

// Dedicated Spoolman base URL normalizer. Currently mirrors normalizeUrl but
// kept separate so future Spoolman-specific rules (e.g. default scheme) can
// be added in one place without touching generic URL handling.
export function normalizeSpoolmanBaseUrl(url: string): string {
  let working = url.trim();
  if (!working) return '';
  // Prepend http:// if user omitted scheme (safer default inside container / LAN)
  if (!/^https?:\/\//i.test(working)) {
    working = 'http://' + working;
  }
  // Remove single trailing slash
  working = working.replace(/\/$/, '');
  return working;
}
