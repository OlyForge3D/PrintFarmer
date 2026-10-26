import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import yaml from 'js-yaml';
import { approvedSdkVersion, validateSdk } from './dotnet-sdk-policy.mjs';

export { approvedSdkVersion, validateSdk };

export function sdkPolicyErrors(files) {
  const errors = [];
  try {
    validateSdk(JSON.parse(files.get('global.json') ?? ''));
  } catch (error) {
    errors.push(error.message);
  }
  for (const [path, content] of files) {
    if (path.endsWith('/global.json')) errors.push(`${path}: nested SDK selector is forbidden`);
    if (/^\.github\/(?:workflows\/|actions\/).+\.ya?ml$/.test(path)) {
      let document;
      try {
        document = yaml.load(content);
      } catch (error) {
        errors.push(`${path}: invalid YAML: ${error.message}`);
        continue;
      }
      const stepLists = [
        ...Object.entries(document?.jobs ?? {}).map(([jobName, job]) => ({
          steps: job.steps ?? [],
          source: path === '.github/workflows/consolidated-release.yml' && jobName === 'build',
        })),
        { steps: document?.runs?.steps ?? [], source: false },
      ];
      for (const { steps, source } of stepLists) {
        const globalJson = source ? 'source/global.json' : 'global.json';
        const verification = `python3 scripts/dotnet-sdk.py verify${source ? ' --root source' : ''}`;
        for (const [index, step] of steps.entries()) {
          if (!step.uses?.startsWith('actions/setup-dotnet@')) continue;
          const inputs = step.with ?? {};
          if (inputs['global-json-file'] !== globalJson ||
              'dotnet-version' in inputs || 'dotnet-quality' in inputs) {
            errors.push(`${path}: setup-dotnet must use only global-json-file: ${globalJson} for SDK selection`);
          }
          const verify = steps[index + 1];
          if (verify?.run !== verification ||
              verify.if !== step.if || verify['continue-on-error'] || verify['working-directory']) {
            errors.push(`${path}: setup-dotnet must immediately verify/log the selected SDK using ${globalJson}`);
          }
        }
      }
    }
    if (/\.(?:props|targets|csproj)$/.test(path) &&
        /<(?:NETCoreSdkVersion|MSBuildSDKsPath|DotNetSdkVersion)>/.test(content)) {
      errors.push(`${path}: MSBuild must not override the global.json SDK selection`);
    }
    const executableConfig = /^(?:scripts\/|\.devcontainer\/|\.github\/(?:workflows|actions)\/)/.test(path) &&
      /(?:\.(?:sh|ps1|conf|ya?ml|json|mjs|cjs|js)|(?:^|\/)Dockerfile(?:\.[^/]+)?)$/.test(path);
    if (!executableConfig || /(?:^|\/)(?:tests|fixtures)\//.test(path) ||
        path.endsWith('/dotnet-install.sh') || path === 'scripts/ci/check-dotnet-sdk.mjs') continue;
    const lines = content.split(/\r?\n/);
    for (let index = 0; index < lines.length; index++) {
      const lineNumber = index + 1;
      let line = lines[index];
      while (/[\\`]$/.test(line) && index + 1 < lines.length) {
        line = `${line.slice(0, -1)} ${lines[++index].trimStart()}`;
      }
      if (/^\s*#/.test(line)) continue;
      const imageTags = [...line.matchAll(/\b(?:mcr\.microsoft\.com\/dotnet\/)?sdk:([^\s"'|,;]+)/gi)];
      const literalImage = imageTags.some(([, tag]) =>
        !(tag === '*' && /\[\[.*==\s+sdk:\*/.test(line)) &&
        !/^(?:\$\{SDK_TAG\}|\$SDK_TAG|\$SdkTag)(?:-upgraded)?$/.test(tag));
      const literalSelector = /\b(?:DOTNET_VERSION|DOTNET_SDK_VERSION|REQ_DOTNET_VERSION|SDK_VERSION|SDK_TAG|sdkVersion|sdkTag)\s*[:=]\s*["'`]?(?:\d|latest)/i.test(line);
      const installSelector = /dotnet-install[^\n]*(?:--(?:channel|version)|-(?:Channel|Version))\s+["']?\d/i.test(line);
      const override = /\$env:DOTNET_VERSION|\$\{DOTNET_VERSION[:-]/.test(line);
      if (literalImage || literalSelector || installSelector || override ||
          line.includes('ghcr.io/devcontainers/features/dotnet:')) {
        errors.push(`${path}:${lineNumber}: independent SDK selector; derive it from global.json`);
      }
    }
    if (path.includes('Dockerfile') && content.includes('mcr.microsoft.com/dotnet/sdk:')) {
      if (!/^ARG SDK_TAG\s*$/m.test(content) || !/COPY global\.json\b/.test(content) ||
          !/dotnet --version/.test(content)) {
        errors.push(`${path}: SDK stages require SDK_TAG without a default, global.json and dotnet --version`);
      }
    }
  }
  try {
    const updates = yaml.load(files.get('.github/dependabot.yml') ?? '').updates ?? [];
    if (!updates.some(update => update['package-ecosystem'] === 'dotnet-sdk' && update.directory === '/')) {
      errors.push('.github/dependabot.yml: root dotnet-sdk updates are required');
    }
  } catch (error) {
    errors.push(`.github/dependabot.yml: ${error.message}`);
  }
  return errors;
}

export function repositorySdkFiles(root) {
  const paths = execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard'],
    { cwd: root, encoding: 'utf8' }).split('\0').filter(Boolean);
  return new Map([...new Set(paths)].filter(path =>
    /(?:^|\/)global\.json$/.test(path) ||
    /\.(?:props|targets|csproj)$/.test(path) ||
    /^(?:scripts\/|\.devcontainer\/|\.github\/(?:workflows|actions)\/|\.github\/dependabot\.yml$)/.test(path) &&
      !/(?:^|\/)(?:tests|fixtures)\//.test(path) &&
      /(?:\.(?:sh|ps1|conf|ya?ml|json|mjs|cjs|js)|(?:^|\/)Dockerfile(?:\.[^/]+)?)$/.test(path)
  ).map(path => [path, readFileSync(join(root, path), 'utf8')]));
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const root = resolve(process.argv[2] ?? '.');
  const errors = sdkPolicyErrors(repositorySdkFiles(root));
  if (errors.length) {
    console.error(errors.join('\n'));
    process.exitCode = 1;
  } else {
    console.log(`SDK selectors agree with global.json (${approvedSdkVersion(root)}); no roll-forward`);
  }
}
