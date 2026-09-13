export const rehearsalAdmissionSource = `const env = process.env;
const repository = 'OlyForge3D/PrintFarmer';
const workflow = '.github/workflows/release-protection-rehearsal.yml';
const branch = { insider: 'development', stable: 'main' }[env.REHEARSAL_CHANNEL];
const trusted = ['insider', 'stable'].includes(env.REHEARSAL_CHANNEL) &&
  env.GITHUB_REPOSITORY === repository &&
  env.GITHUB_EVENT_NAME === 'workflow_dispatch' &&
  env.GITHUB_REF === \`refs/heads/\${branch}\` &&
  env.GITHUB_WORKFLOW_REF === \`\${repository}/\${workflow}@refs/heads/\${branch}\` &&
  /^[a-f0-9]{40}$/.test(env.GITHUB_SHA ?? '') &&
  env.GITHUB_WORKFLOW_SHA === env.GITHUB_SHA &&
  env.GITHUB_RUN_ATTEMPT === '1' &&
  /^[1-9][0-9]*$/.test(env.GITHUB_RUN_ID ?? '');
if (!trusted) {
  console.error('Untrusted rehearsal dispatch; protected jobs blocked');
  process.exitCode = 1;
}
`;
