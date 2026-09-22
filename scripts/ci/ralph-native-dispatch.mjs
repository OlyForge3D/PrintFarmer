import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { digest, taskFromEvidence } from './ralph-mailbox.mjs';

const fail = (message) => { throw new Error(`Native dispatch blocked: ${message}`); };
const sha256 = (text) => createHash('sha256').update(text).digest('hex');

export function validateClassification(evidence) {
  if (!['general', 'mobile', 'mixed', 'unknown'].includes(evidence?.scope) ||
      typeof evidence.classificationComplete !== 'boolean') {
    fail('Explicit scope and classificationComplete required; capabilities are not category evidence.');
  }
}

export function validateNativeCapabilities(evidence) {
  const native = evidence?.nativeCapabilities;
  if (native?.createSession !== true || !Array.isArray(native.agents) ||
      !native.agents.includes('Squad') || !native.models || typeof native.models !== 'object' ||
      Array.isArray(native.models)) {
    fail('Supported create_session, registered Squad agent and advertised model/effort capabilities required.');
  }
  return native;
}

export async function buildDispatchPlan({ config, evidence, assignment, correlation, owner, cwd }) {
  validateClassification(evidence);
  const native = validateNativeCapabilities(evidence);
  if (assignment.task.pr && native.openPrSession !== true) fail('Existing PR recovery requires supported open_pr_session, never a fresh branch.');
  if (digest(taskFromEvidence(evidence)) !== assignment.taskDigest) fail('Assignment task changed.');
  const member = owner.slice('squad:'.length);
  if (!/^(dallas|ripley|drake|lambert|hudson|gorman|kane|ash|brett|parker|newt|copilot)$/.test(member)) fail('Invalid specialist.');
  const charterPath = member === 'copilot' ? '.github/copilot-instructions.md' : `.squad/agents/${member}/charter.md`;
  const [squad, hosts, charter, agent, clauses] = await Promise.all([
    readFile(path.join(cwd, '.squad/config.json'), 'utf8').then(JSON.parse),
    readFile(path.join(cwd, '.copilot/skills/ralph-loop/hosts.json'), 'utf8').then(JSON.parse),
    readFile(path.join(cwd, charterPath), 'utf8'),
    readFile(path.join(cwd, '.github/agents/squad.agent.md'), 'utf8'),
    config.host === 'macos-mobile'
      ? readFile(path.join(cwd, '.copilot/skills/ralph-loop/macos-kickoff.md'), 'utf8') : '',
  ]);
  if (!/^name: Squad$/m.test(agent) || !agent.includes('RALPH-ASSIGNED-WORKER-V1') || !charter.trim()) {
    fail('Registered Squad bounded-worker entrypoint and nonempty specialist charter required.');
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
    member, charterPath, charterSha256: sha256(charter), model, reasoningEffort: effort,
    purpose: assignment.task.purpose, category: assignment.task.category,
    issue: assignment.task.issue, pr: assignment.task.pr,
  };
  const prompt = `RALPH-ASSIGNED-WORKER-V1
Act as the assigned Squad specialist, not as a coordinator. Read
.copilot/skills/ralph-loop/assigned-worker.md and verify this packet/charter.
Return startup-only ACK; do not begin substantive work until consumer continuation.
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
${clauses}`;
  const createSession = {
    project_id: config.projectId, workspace_type: 'worktree',
    base_branch: 'development', coordinate_with_creator: true, notify_on_idle: 'once',
    name: `${member} ${packet.purpose} ${packet.issue ?? packet.pr}`.slice(0, 40),
    kickoff: { agent: 'Squad', model, reasoning_effort: effort, mode: 'autopilot', prompt },
  };
  const nativeTool = assignment.task.pr ? 'open_pr_session' : 'create_session';
  const nativeArguments = assignment.task.pr ? {
    repo_full_name: 'OlyForge3D/PrintFarmer', pr_number: assignment.task.pr,
    coordinate_with_creator: true, notify_on_idle: 'once', kickoff: createSession.kickoff,
  } : createSession;
  const plan = { version: 1, packet, nativeTool, nativeArguments, continuation };
  return { ...plan, planDigest: digest(plan) };
}

export function validateStartup(plan, evidence) {
  const ack = evidence.startupAck;
  const packet = plan.packet;
  for (const key of ['assignmentId', 'generation', 'taskDigest', 'correlation', 'member', 'charterSha256']) {
    if (ack?.[key] !== packet[key]) fail(`Startup ACK does not match ${key}.`);
  }
  if (ack.substantiveWorkStarted !== false || ack.noChildren !== true) fail('Startup-only ACK with no children required.');
  const configuration = evidence.configuration;
  if (!['successful-native-create', 'native-readback', 'owner-attestation'].includes(configuration?.source) ||
      configuration.model !== packet.model || configuration.reasoningEffort !== packet.reasoningEffort ||
      evidence.dispatchPlanDigest !== plan.planDigest ||
      (ack.actualModel !== undefined && ack.actualModel !== packet.model) ||
      (ack.actualReasoningEffort !== undefined && ack.actualReasoningEffort !== packet.reasoningEffort)) {
    fail('Exact model/effort configuration evidence required; failed kickoff does not preserve requested settings.');
  }
}
