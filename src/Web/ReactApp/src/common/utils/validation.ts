// Simple validation utilities shared between SetupWizard and SettingsPage
// Keep lightweight to avoid pulling external libs.

export function isValidCidr(input: string): boolean {
  if (!input) return false;
  const match = input.match(/^([0-9]{1,3})(?:\.([0-9]{1,3})){3}\/(\d{1,2})$/);
  if (!match) return false;
  const parts = input.split('/');
  const ip = parts[0].split('.').map(n => Number(n));
  const prefix = Number(parts[1]);
  
  // Basic format validation
  if (prefix < 0 || prefix > 32) return false;
  if (!ip.every(oct => oct >= 0 && oct <= 255)) return false;
  
  // Validate that the IP is actually a network address (not a host address)
  // For example, 10.0.0.5/24 should be 10.0.0.0/24
  const networkIp = getNetworkAddress(ip, prefix);
  return networkIp.every((oct, idx) => oct === ip[idx]);
}

// Get the network address from an IP and prefix length
function getNetworkAddress(ip: number[], prefixLength: number): number[] {
  const networkIp = [...ip];
  const bitsToZero = 32 - prefixLength;
  
  // Zero out the host bits
  for (let i = 3; i >= 0; i--) {
    const bitsInOctet = Math.min(8, Math.max(0, bitsToZero - (3 - i) * 8));
    if (bitsInOctet > 0) {
      const mask = (0xFF << bitsInOctet) & 0xFF;
      networkIp[i] = networkIp[i] & mask;
    }
  }
  
  return networkIp;
}

// Check if two CIDR ranges overlap
export function doCidrRangesOverlap(cidr1: string, cidr2: string): boolean {
  if (!isValidCidr(cidr1) || !isValidCidr(cidr2)) return false;
  
  const [ip1, prefix1] = parseCidr(cidr1);
  const [ip2, prefix2] = parseCidr(cidr2);
  
  // Check if either network contains the other
  return isIpInNetwork(ip1, ip2, prefix2) || isIpInNetwork(ip2, ip1, prefix1);
}

// Parse CIDR notation into IP array and prefix length
function parseCidr(cidr: string): [number[], number] {
  const [ipStr, prefixStr] = cidr.split('/');
  const ip = ipStr.split('.').map(n => Number(n));
  const prefix = Number(prefixStr);
  return [ip, prefix];
}

// Check if an IP address is within a network range
function isIpInNetwork(ip: number[], networkIp: number[], prefixLength: number): boolean {
  const networkAddr = getNetworkAddress(networkIp, prefixLength);
  const testAddr = getNetworkAddress(ip, prefixLength);
  
  return testAddr.every((oct, idx) => oct === networkAddr[idx]);
}

// Get overlapping CIDR ranges from a list
export function findOverlappingCidrRanges(cidrs: string[]): string[] {
  const validCidrs = cidrs.filter(cidr => cidr.trim() && isValidCidr(cidr.trim()));
  const overlapping: string[] = [];
  
  for (let i = 0; i < validCidrs.length; i++) {
    for (let j = i + 1; j < validCidrs.length; j++) {
      if (doCidrRangesOverlap(validCidrs[i], validCidrs[j])) {
        if (!overlapping.includes(validCidrs[i])) overlapping.push(validCidrs[i]);
        if (!overlapping.includes(validCidrs[j])) overlapping.push(validCidrs[j]);
      }
    }
  }
  
  return overlapping;
}

// Suggest the correct network address for a given CIDR
export function suggestCorrectNetworkAddress(cidr: string): string | null {
  if (!cidr.includes('/')) return null;
  
  const match = cidr.match(/^([0-9]{1,3})(?:\.([0-9]{1,3})){3}\/(\d{1,2})$/);
  if (!match) return null;
  
  const parts = cidr.split('/');
  const ip = parts[0].split('.').map(n => Number(n));
  const prefix = Number(parts[1]);
  
  if (prefix < 0 || prefix > 32) return null;
  if (!ip.every(oct => oct >= 0 && oct <= 255)) return null;
  
  const networkIp = getNetworkAddress(ip, prefix);
  return `${networkIp.join('.')}/${prefix}`;
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
