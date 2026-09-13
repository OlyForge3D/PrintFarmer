import { qualificationClient, qualificationWorkflow, verifyQualification, recordQualification } from './canonical-qualification.mjs';
import { repository, requireThat } from './release-policy.mjs';

export async function runQualificationControl(operation, env = process.env) {
  requireThat(['verify', 'record'].includes(operation), 'Unknown qualification operation');
  const api = qualificationClient(env.GH_TOKEN, operation === 'record');
  if (operation === 'record') return recordQualification(api, env.QUALIFICATION_RUN_ID, env);
  const evidence = await verifyQualification(api, env.GITHUB_RUN_ID, env.RELEASE_APPROVAL_MODE, Date.now(), false);
  requireThat(env.GITHUB_REPOSITORY === repository && env.GITHUB_EVENT_NAME === 'workflow_dispatch' &&
    env.GITHUB_RUN_ATTEMPT === '1' && env.GITHUB_SHA === evidence.trustedHead &&
    env.GITHUB_WORKFLOW_SHA === evidence.trustedHead &&
    env.GITHUB_REF === `refs/heads/${evidence.defaultBranch}` &&
    env.GITHUB_WORKFLOW_REF === `${repository}/${qualificationWorkflow}@refs/heads/${evidence.defaultBranch}`,
  'Qualification must run the live trusted default-branch workflow');
  return evidence;
}

if (process.argv[1]?.endsWith('canonical-qualification-control.mjs')) {
  runQualificationControl(process.argv[2]).then(evidence => console.log(JSON.stringify(evidence)))
    .catch(error => { console.error(error.message); process.exitCode = 1; });
}
