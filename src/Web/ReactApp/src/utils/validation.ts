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
  return url.trim().replace(/\/$/, '');
}
