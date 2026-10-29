import { readFileSync } from 'node:fs';
import { join } from 'node:path';

export function validateSdk(globalJson) {
  const sdk = globalJson.sdk;
  if (!/^10\.0\.\d{3}$/.test(sdk?.version ?? '') || Number(sdk.version.split('.')[2]) < 200) {
    throw new Error('global.json must approve a stable .NET 10 SDK >=10.0.200');
  }
  if (sdk.rollForward !== 'disable' || sdk.allowPrerelease !== false) {
    throw new Error('global.json requires rollForward:disable and allowPrerelease:false');
  }
  return sdk.version;
}

export function approvedSdkVersion(root) {
  return validateSdk(JSON.parse(readFileSync(join(root, 'global.json'), 'utf8')));
}
