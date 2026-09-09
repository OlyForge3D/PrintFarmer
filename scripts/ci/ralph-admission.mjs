import {
  RalphMacSshError, acknowledgeLocalJob, dispatchMacJob, recordLocalTerminalResult,
  recordTerminalResult, recoverLocalReservation, recoverRemoteDelivery, reserveLocalJob,
} from './ralph-macos-ssh.mjs';

const commands = Object.assign(Object.create(null), {
  'reserve-local': ({ job, eligibility, controllerPid }) => {
    if (!Number.isInteger(controllerPid) || controllerPid <= 0) {
      throw new RalphMacSshError('reserve-local requires the Ralph controller process identifier.', 'INVALID_REQUEST');
    }
    return reserveLocalJob({ job, eligibility, controllerPid });
  },
  'acknowledge-local': ({ jobId, sessionId }) => acknowledgeLocalJob(jobId, sessionId),
  'recover-local': ({ jobId, sessionAbsent }) => recoverLocalReservation(jobId, { sessionAbsent }),
  'terminal-local': ({ result }) => recordLocalTerminalResult(result),
  'dispatch-remote': ({ job, eligibility }) => dispatchMacJob({ job, eligibility }),
  'recover-remote': ({ jobId }) => recoverRemoteDelivery(jobId),
  'terminal-remote': ({ result }) => recordTerminalResult(result),
});

async function readRequest() {
  const input = await new Promise((resolve, reject) => {
    let content = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => { content += chunk; });
    process.stdin.on('end', () => resolve(content));
    process.stdin.on('error', reject);
  });
  if (input.length > 128 * 1024) throw new RalphMacSshError('Admission request exceeds the limit.', 'INVALID_REQUEST');
  let request;
  try {
    request = JSON.parse(input);
  } catch {
    throw new RalphMacSshError('Admission request must be JSON.', 'INVALID_REQUEST');
  }
  if (!request || typeof request !== 'object' || Array.isArray(request)) {
    throw new RalphMacSshError('Admission request must be an object.', 'INVALID_REQUEST');
  }
  return request;
}

async function main() {
  const command = process.argv[2];
  const execute = commands[command];
  if (!execute || process.argv.length !== 3) {
    throw new RalphMacSshError('Usage: ralph-admission.mjs <reserve-local|acknowledge-local|recover-local|terminal-local|dispatch-remote|recover-remote|terminal-remote>.', 'INVALID_COMMAND');
  }
  const request = await readRequest();
  const result = await execute(request);
  process.stdout.write(`${JSON.stringify({ ok: true, result })}\n`);
}

main().catch((error) => {
  const code = error instanceof RalphMacSshError ? error.code : 'ADMISSION_FAILURE';
  process.stderr.write(`${JSON.stringify({ ok: false, code, message: error.message })}\n`);
  process.exitCode = 1;
});
