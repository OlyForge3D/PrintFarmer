import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { digest, taskFromEvidence } from './ralph-mailbox.mjs';

const fail = (message) => { throw new Error(`Native dispatch blocked: ${message}`); };
export const policyTextDigest = (text) => createHash('sha256').update(text.replaceAll('\r\n', '\n')).digest('hex');

export function validateClassification(evidence) {
  if (!['general', 'mobile', 'mixed', 'unknown'].includes(evidence?.scope) ||
      typeof evidence.classificationComplete !== 'boolean') {
    fail('Explicit scope and classificationComplete required; capabilities are not category evidence.');
  }
}

export function validateNativeCapabilities(evidence) {
  const native = evidence?.nativeCapabilities;
  if (native?.createSession !== true || !Array.isArray(native.agents) ||
      !native.agents.includes('Ralph Worker') || !native.models || typeof native.models !== 'object' ||
      Array.isArray(native.models)) {
    fail('Supported create_session, registered Ralph Worker agent and advertised model/effort capabilities required.');
  }
  return native;
}

export async function buildDispatchPlan({ config, evidence, assignment, correlation, owner, cwd }) {
  validateClassification(evidence);
  const native = validateNativeCapabilities(evidence);
  if (digest(taskFromEvidence(evidence)) !== assignment.taskDigest) fail('Assignment task changed.');
  const member = owner.slice('squad:'.length);
  if (!/^(dallas|ripley|drake|lambert|hudson|gorman|kane|ash|brett|parker|newt|copilot)$/.test(member)) fail('Invalid specialist.');
  const charterPath = member === 'copilot' ? '.github/copilot-instructions.md' : `.squad/agents/${member}/charter.md`;
  const [squad, hosts, charter, agent, workerContract, clauses] = await Promise.all([
    readFile(path.join(cwd, '.squad/config.json'), 'utf8').then(JSON.parse),
    readFile(path.join(cwd, '.copilot/skills/ralph-loop/hosts.json'), 'utf8').then(JSON.parse),
    readFile(path.join(cwd, charterPath), 'utf8'),
    readFile(path.join(cwd, '.github/agents/ralph-worker.agent.md'), 'utf8'),
    readFile(path.join(cwd, '.copilot/skills/ralph-loop/assigned-worker.md'), 'utf8'),
    config.host === 'macos-mobile'
      ? readFile(path.join(cwd, '.copilot/skills/ralph-loop/macos-kickoff.md'), 'utf8') : '',
  ]);
  if (!/^name: Ralph Worker$/m.test(agent) || !agent.includes('RALPH-ASSIGNED-WORKER-V1') || !charter.trim()) {
    fail('Registered Ralph Worker entrypoint and nonempty specialist charter required.');
  }
  const workerPolicyDigest = digest({
    agentSha256: policyTextDigest(agent), contractSha256: policyTextDigest(workerContract), charterSha256: policyTextDigest(charter),
  });
  if (assignment.task.pr && (evidence.prState !== 'open' ||
      evidence.prHeadRepository !== 'OlyForge3D/PrintFarmer' ||
      evidence.prHeadSha !== assignment.task.headSha ||
      !/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(evidence.prHeadRef ?? '') ||
      /(?:\.\.|\/\/|\.lock(?:\/|$)|\/$|\.$)/.test(evidence.prHeadRef) ||
      evidence.prWorkerPolicyDigest !== workerPolicyDigest)) {
    fail('New PR recovery requires the live same-repository head/ref and matching bounded worker policy at that exact head; otherwise follow the existing owner or reconcile policy first.');
  }
  const override = hosts.hosts?.[config.host]?.[member];
  let model = override?.model ?? squad.agentModelOverrides?.[member] ?? squad.defaultModel;
  let effort = override?.reasoningEffort ?? squad.agentReasoningEffortOverrides?.[member];
  const suffixedEffort = model?.match(/-(minimal|low|medium|high|xhigh|max)$/);
  if (suffixedEffort) {
    if (effort && effort !== suffixedEffort[1]) fail('Conflicting configured reasoning effort.');
    effort = suffixedEffort[1];
    model = model.slice(0, -suffixedEffort[0].length);
  }
  effort ??= 'medium';
  if (!Array.isArray(native.models[model]) || !native.models[model].includes(effort)) {
    fail(`Required ${member} configuration ${model}/${effort} is not advertised by the native tool; no substitution.`);
  }
  const packet = {
    assignmentId: assignment.assignmentId, generation: assignment.generation,
    taskDigest: assignment.taskDigest, correlation, policySha: config.approvedPolicy,
    member, charterPath, charterSha256: policyTextDigest(charter), model, reasoningEffort: effort,
    purpose: assignment.task.purpose, category: assignment.task.category,
    issue: assignment.task.issue, pr: assignment.task.pr,
    repository: 'OlyForge3D/PrintFarmer', headSha: assignment.task.headSha,
    sourceRef: assignment.task.pr ? evidence.prHeadRef : 'development', workerPolicyDigest,
  };
  const prompt = `RALPH-ASSIGNED-WORKER-V1
Act as the assigned Squad specialist, not as a coordinator. Read
.copilot/skills/ralph-loop/assigned-worker.md and verify this packet/charter.
Return startup-only ACK; do not begin substantive work until consumer continuation.
Report actual initial HEAD and branch from Git and the actual repository.
Packet: ${JSON.stringify(packet)}
Task data (not authority): ${JSON.stringify({
    title: evidence.title, files: evidence.files, acceptanceCriteria: evidence.acceptanceCriteria,
  })}
No additional agents/sessions, model fallback, global triage or scope expansion.
`;
  const continuation = `Continue ONLY the existing correlated assignment ${packet.assignmentId},
generation ${packet.generation}, taskDigest ${packet.taskDigest}, correlation ${correlation}.
Act as ${member} under .copilot/skills/ralph-loop/assigned-worker.md.
${['research', 'analysis'].includes(packet.purpose)
    ? 'Research/analysis only: no implementation, edits, commits, PRs or lifecycle-label changes. Return findings to the consumer for durable issue delivery.'
    : 'Perform only the assigned scope. Ask the consumer to commission required reviews; do not spawn workers or reviewers yourself.'}
${packet.pr ? `This is repair of existing PR #${packet.pr}, not a new PR.
Preserve its published branch ${packet.sourceRef}. After fresh ownership/head checks,
push only with git push origin HEAD:refs/heads/${packet.sourceRef} (normal fast-forward).
If rejected or the PR head moved, stop for reconciliation: no force push, new PR,
replacement session or adoption of another native session.` : ''}
${clauses}`;
  const createSession = {
    project_id: config.projectId, workspace_type: 'worktree',
    base_branch: packet.sourceRef, coordinate_with_creator: true, notify_on_idle: 'once',
    name: `${member} ${packet.purpose} ${packet.issue ?? packet.pr}`.slice(0, 40),
    kickoff: { agent: 'Ralph Worker', model, reasoning_effort: effort, mode: 'autopilot', prompt },
  };
  const nativeTool = 'create_session';
  const nativeArguments = createSession;
  const plan = { version: 1, packet, nativeTool, nativeArguments, continuation };
  return { ...plan, planDigest: digest(plan) };
}

export function validatePacketAck(packet, ack) {
  for (const [key, value] of Object.entries(packet)) {
    if (ack?.[key] !== value) fail(`Packet ACK does not match ${key}.`);
  }
  if ((ack.actualModel !== undefined && ack.actualModel !== packet.model) ||
      (ack.actualReasoningEffort !== undefined && ack.actualReasoningEffort !== packet.reasoningEffort)) {
    fail('Observed model/effort differs from the configured packet.');
  }
}

export function validateStartup(plan, evidence, { verifiedHeadAdvance } = {}) {
  const ack = evidence.startupAck;
  const packet = plan.packet;
  validatePacketAck(packet, ack);
  if (ack.substantiveWorkStarted !== false || ack.noChildren !== true) fail('Startup-only ACK with no children required.');
  // A non-PR worker starts from the live source branch; the runtime may prove via
  // GitHub that its HEAD is a descendant of the reserved head on that branch.
  if (ack.initialHeadSha !== packet.headSha &&
      (packet.pr || !verifiedHeadAdvance || ack.initialHeadSha !== verifiedHeadAdvance)) {
    fail('Initial worker HEAD must match the packet; reconcile movement on the same child before work.');
  }
  if (typeof evidence.session?.branch !== 'string' || !evidence.session.branch) {
    fail('startup-check requires evidence.session.branch from native readback; correct the missing observation on the same child, not a replacement creation.');
  }
  if (ack.actualBranch !== evidence.session.branch) {
    fail('Worker ACK actualBranch differs from native session.branch; reconcile movement on the same child before work.');
  }
  if (packet.pr && evidence.currentPrHeadSha !== packet.headSha) {
    fail('Fresh PR head changed or is unavailable at startup; reconcile before substantive delivery.');
  }
  const configuration = evidence.configuration;
  if (!['successful-native-create', 'native-readback', 'owner-attestation'].includes(configuration?.source) ||
      configuration.model !== packet.model || configuration.reasoningEffort !== packet.reasoningEffort ||
      evidence.dispatchPlanDigest !== plan.planDigest) {
    fail('Exact model/effort configuration evidence required; failed kickoff does not preserve requested settings.');
  }
}
