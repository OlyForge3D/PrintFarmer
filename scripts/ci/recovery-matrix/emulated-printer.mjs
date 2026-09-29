// Emulated-printer helpers for the #3103 physical-reconciliation cell. The printer is always
// the repository's Moonraker emulator on the run's internal (egress-denied) network — never a
// real endpoint — and every observation is read from the emulator's own request log.

import { createHash, createHmac, randomUUID } from 'node:crypto';

export const emulatorPort = 7125;
export const emulatorHostOctet = 40;
export const autoDispatchDurableScanIntervalMs = 30_000;
export const fencedConsumerPollWindowMs = 35_000;
export const postReconciliationDispatchWindowMs = 45_000;

const nameIdentifierClaim = 'http://schemas.microsoft.com/ws/2008/05/identity/claims/nameidentifier';
const roleClaim = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
const farmAdminRole = 'farm_admin';

export function emulatorIpFor(appIp) {
  const octets = String(appIp).split('.');
  if (octets.length !== 4 || octets.some((octet) => !/^\d{1,3}$/.test(octet) || Number(octet) > 255)) {
    throw new Error(`invalid_app_ip:${appIp}`);
  }
  if (Number(octets[3]) === emulatorHostOctet) throw new Error(`emulator_ip_collides_with_app:${appIp}`);
  return [...octets.slice(0, 3), String(emulatorHostOctet)].join('.');
}

// Points the single seeded placeholder printer at the emulator as a Moonraker backend.
export function pointPrinterAtEmulatorSql(emulatorIp) {
  if (!/^\d{1,3}(\.\d{1,3}){3}$/.test(emulatorIp)) throw new Error(`invalid_emulator_ip:${emulatorIp}`);
  return `UPDATE "Printers" SET "ServerUrl" = 'http://${emulatorIp}', "BackendPort" = ${emulatorPort}, `
    + `"Backend" = 1, "IsEnabled" = true, "IsAvailable" = true, "InMaintenance" = false, `
    + `"AutoDispatchEnabled" = true, "Name" = 'recovery-matrix-emulated-printer' `
    + `RETURNING "Id";`;
}

export const queuedAutoDispatchGcode = [
  '; recovery matrix queued auto-dispatch fixture',
  'G28',
  'G1 X5 Y5 Z0.3 F3000',
  'M84',
  '',
].join('\n');

export function queuedAutoDispatchWork({ printerId }) {
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(printerId)) {
    throw new Error(`invalid_printer_id:${printerId}`);
  }

  const jobId = randomUUID();
  const gcodeFileId = randomUUID();
  const fileName = `${gcodeFileId}.gcode`;
  const fileHash = createHash('sha256').update(queuedAutoDispatchGcode).digest('hex');
  return {
    jobId,
    gcodeFileId,
    fileName,
    fileHash,
    fileSizeBytes: Buffer.byteLength(queuedAutoDispatchGcode),
    contentBase64: Buffer.from(queuedAutoDispatchGcode, 'utf8').toString('base64'),
  };
}

export function queuedAutoDispatchWorkSql({
  printerId,
  jobId,
  gcodeFileId,
  fileName,
  fileHash,
  fileSizeBytes,
}) {
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (!uuid.test(printerId)) throw new Error(`invalid_printer_id:${printerId}`);
  if (!uuid.test(jobId)) throw new Error(`invalid_job_id:${jobId}`);
  if (!uuid.test(gcodeFileId)) throw new Error(`invalid_gcode_file_id:${gcodeFileId}`);
  if (!/^[0-9a-f]{64}$/i.test(fileHash)) throw new Error(`invalid_file_hash:${fileHash}`);
  if (!/^[0-9a-f-]+\.gcode$/i.test(fileName)) throw new Error(`invalid_file_name:${fileName}`);
  if (!Number.isInteger(fileSizeBytes) || fileSizeBytes <= 0) throw new Error(`invalid_file_size:${fileSizeBytes}`);

  return `
WITH root AS (
  INSERT INTO "FolderNode" ("Id", "Path", "FolderType", "CreatedAt", "DeletedAt")
  VALUES (gen_random_uuid(), '/', 'gcode', now(), NULL)
  ON CONFLICT ("Path", "FolderType") DO UPDATE SET "DeletedAt" = NULL
  RETURNING "Id"
), folder AS (
  SELECT "Id" FROM root
  UNION ALL
  SELECT "Id" FROM "FolderNode" WHERE "Path" = '/' AND "FolderType" = 'gcode' LIMIT 1
), settings AS (
  INSERT INTO "DispatchSettings" (
    "Id", "AutoDispatchEnabled", "AutoDispatchMode", "CreatedDate", "IdleThresholdSeconds",
    "LoadBalancingStrategy", "MaxConcurrentDispatches", "MinimumScoreThreshold",
    "Revision", "UpdatedAt", "UpdatedDate")
  VALUES (1, true, 'Auto', now(), 30, 'BestFit', 3, 0, 1, now(), now())
  ON CONFLICT ("Id") DO UPDATE SET
    "AutoDispatchEnabled" = true,
    "AutoDispatchMode" = 'Auto',
    "IdleThresholdSeconds" = 30,
    "MinimumScoreThreshold" = 0,
    "UpdatedAt" = now(),
    "UpdatedDate" = now()
), printer_ready AS (
  UPDATE "Printers" SET
    "IsEnabled" = true,
    "IsAvailable" = true,
    "InMaintenance" = false,
    "AutoDispatchEnabled" = true
  WHERE "Id" = '${printerId}'::uuid
), dispatch_state AS (
  INSERT INTO "PrinterDispatchStates" (
    "PrinterId", "AutoDispatchState", "BedPreConfirmed", "QueueRevision", "Revision",
    "PhysicalControlRequiresReconciliation")
  VALUES ('${printerId}'::uuid, 2, true, 1, 1, false)
  ON CONFLICT ("PrinterId") DO UPDATE SET
    "AutoDispatchState" = 2,
    "BedPreConfirmed" = true,
    "QueueRevision" = "PrinterDispatchStates"."QueueRevision" + 1,
    "PhysicalControlCommandId" = NULL,
    "PhysicalControlAttemptId" = NULL,
    "PhysicalControlOperation" = NULL,
    "PhysicalControlActorSubject" = NULL,
    "PhysicalControlStartedAtUtc" = NULL,
    "PhysicalControlRequiresReconciliation" = false
), gcode AS (
  INSERT INTO "GcodeFiles" (
    "Id", "Name", "FileName", "FolderId", "FilePath", "FileSizeBytes", "FileHash",
    "UploadedAt", "CreatedAt", "UpdatedAt", "Source", "HealthStatus")
  SELECT '${gcodeFileId}'::uuid, 'recovery-matrix-queued.gcode', '${fileName}', "Id", '/',
    ${fileSizeBytes}, '${fileHash}', now(), now(), now(), 0, 1
  FROM folder
  ON CONFLICT ("Id") DO UPDATE SET
    "FileName" = EXCLUDED."FileName",
    "FilePath" = EXCLUDED."FilePath",
    "FileSizeBytes" = EXCLUDED."FileSizeBytes",
    "FileHash" = EXCLUDED."FileHash",
    "UpdatedAt" = now()
)
INSERT INTO "PrintJobs" (
  "Id", "Name", "GcodeFileId", "AssignedPrinterId", "Status", "Priority", "QueuePosition",
  "CreatedAt", "UpdatedAt", "QueuedAt", "IsExternalPrint")
VALUES (
  '${jobId}'::uuid, 'recovery-matrix-queued.gcode', '${gcodeFileId}'::uuid, NULL, 0, 3, 1,
  now(), now(), now() - interval '1 second', false)
RETURNING "Id";`;
}

// Short-lived farm_admin bearer token minted with the throwaway run's own signing key. It carries
// no permissions beyond what the probe needs to reach the admission check and is never persisted.
export function mintHarnessJwt({ key, issuer, audience, nowSeconds = Math.floor(Date.now() / 1000) }) {
  if (!key || key.length < 32) throw new Error('harness_jwt_key_missing');
  const encode = (value) => Buffer.from(JSON.stringify(value)).toString('base64url');
  const subject = randomUUID();
  const header = encode({ alg: 'HS256', typ: 'JWT' });
  const payload = encode({
    sub: subject,
    [nameIdentifierClaim]: subject,
    role: farmAdminRole,
    [roleClaim]: farmAdminRole,
    iss: issuer,
    aud: audience,
    iat: nowSeconds,
    nbf: nowSeconds - 30,
    exp: nowSeconds + 600,
  });
  const signature = createHmac('sha256', Buffer.from(key, 'utf8')).update(`${header}.${payload}`).digest('base64url');
  return `${header}.${payload}.${signature}`;
}

// Classifies one POST /api/auto-dispatch/{printerId}/ready response. The probe deliberately omits
// the If-Match dispatch precondition, so an admitted request stops at 428 without changing
// dispatch state. `admitted` is only that exact precondition response: any other status or body
// (for example 404 printer_not_found) is `unexpected:status=N` and proves nothing either way.
// `unreachable` means the application was not serving; `unauthenticated` means the probe itself
// was rejected and proves nothing about the fence.
export function classifyDispatchProbe({ status, body }) {
  if (status === -1) return 'unreachable';
  const payload = parseJsonObject(body);
  if (status === 409 && payload?.error === 'host_update_admission_closed') return 'admission-closed';
  if (status === 401 || status === 403) return 'unauthenticated';
  if (status >= 500) return 'server-error';
  if (status === 428 && payload?.error === 'precondition_required' && payload?.detail === 'If-Match is required.') {
    return 'admitted';
  }
  return `unexpected:status=${status}`;
}

function parseJsonObject(body) {
  try {
    const value = JSON.parse(String(body ?? ''));
    return value && typeof value === 'object' && !Array.isArray(value) ? value : null;
  } catch {
    return null;
  }
}

// Python program run inside a throwaway container on the run network. The request spec arrives in
// the REQ environment variable so no value is ever interpolated into code; HTTP errors report
// their status and any transport failure reports -1.
export const networkRequestScript = [
  'import json, os, urllib.request, urllib.error',
  "spec = json.loads(os.environ['REQ'])",
  "data = spec.get('body')",
  "req = urllib.request.Request(spec['url'], method=spec['method'], headers=spec.get('headers', {}), data=None if data is None else data.encode())",
  'try:',
  '    r = urllib.request.urlopen(req, timeout=20)',
  "    print(json.dumps({'status': r.status, 'body': r.read().decode(errors='replace')}))",
  'except urllib.error.HTTPError as e:',
  "    print(json.dumps({'status': e.code, 'body': e.read().decode(errors='replace')}))",
  'except Exception as e:',
  "    print(json.dumps({'status': -1, 'body': type(e).__name__}))",
].join('\n');

export function parseNetworkResponse(stdout) {
  const parsed = JSON.parse(String(stdout).trim().split(/\r?\n/).at(-1));
  if (!Number.isInteger(parsed?.status) || typeof parsed?.body !== 'string') throw new Error('network_response_invalid');
  return parsed;
}

export function parseEmulatorRequests(body) {
  const parsed = JSON.parse(body);
  if (!Number.isInteger(parsed?.total) || !Number.isInteger(parsed?.commands) || !Array.isArray(parsed?.entries)) {
    throw new Error('emulator_request_log_invalid');
  }
  return parsed;
}

// Commands recorded after `baseline` (a previous parseEmulatorRequests result). The cumulative
// counter is authoritative; retained entries only name the offending requests.
export function commandsSince(baseline, current) {
  const count = current.commands - baseline.commands;
  if (count < 0 || current.total < baseline.total) throw new Error('emulator_request_log_regressed');
  const offenders = current.entries
    .filter((entry) => entry.isCommand && entry.sequence > baseline.total)
    .map((entry) => `${entry.transport}:${entry.method}:${entry.target}`);
  return { count, reads: (current.total - baseline.total) - count, offenders };
}

export function queuedAutoDispatchEvidenceSince(baseline, current, fileName) {
  if (!/^[0-9a-f-]+\.gcode$/i.test(fileName)) throw new Error(`invalid_file_name:${fileName}`);
  const observed = commandsSince(baseline, current);
  const commands = current.entries.filter((entry) => entry.isCommand && entry.sequence > baseline.total);
  const upload = commands.find((entry) =>
    entry.transport === 'http' &&
    entry.method === 'POST' &&
    entry.target.startsWith('/server/files/upload:') &&
    entry.target.includes(`/${fileName}:`));
  const start = commands.find((entry) =>
    entry.transport === 'http' &&
    entry.method === 'POST' &&
    entry.target.startsWith('/printer/print/start:') &&
    entry.target.endsWith(fileName));
  return {
    ...observed,
    upload: upload?.target,
    start: start?.target,
    matched: Boolean(upload || start),
  };
}
