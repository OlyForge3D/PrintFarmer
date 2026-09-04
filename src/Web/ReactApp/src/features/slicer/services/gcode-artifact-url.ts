import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';

const ABSOLUTE_URL_PATTERN = /^[a-z][a-z\d+.-]*:/i;
const ARTIFACT_ID_PATTERN =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const INVALID_URL_MESSAGE = 'Invalid G-code artifact URL.';

export function resolveTrustedGcodeArtifactUrl(gcodeUrl: string): string {
  const appOrigin = globalThis.location?.origin;
  if (!appOrigin || gcodeUrl !== gcodeUrl.trim() || gcodeUrl.startsWith('//')) {
    throw new Error(INVALID_URL_MESSAGE);
  }

  const apiBaseUrl = getApiBaseUrl().replace(/\/+$/, '');
  const apiBaseIsAbsolute = ABSOLUTE_URL_PATTERN.test(apiBaseUrl);
  if (ABSOLUTE_URL_PATTERN.test(gcodeUrl) !== apiBaseIsAbsolute) {
    throw new Error(INVALID_URL_MESSAGE);
  }

  try {
    const apiBase = new URL(apiBaseUrl, appOrigin);
    const candidate = new URL(gcodeUrl, appOrigin);
    const artifactPrefix = `${apiBase.pathname}/artifacts/`;
    const artifactId = candidate.pathname.startsWith(artifactPrefix)
      ? candidate.pathname.slice(artifactPrefix.length)
      : '';

    if (
      candidate.origin !== apiBase.origin
      || candidate.protocol !== apiBase.protocol
      || candidate.username
      || candidate.password
      || candidate.search
      || candidate.hash
      || !ARTIFACT_ID_PATTERN.test(artifactId)
    ) {
      throw new Error(INVALID_URL_MESSAGE);
    }

    const trustedPath = `${artifactPrefix}${artifactId}`;
    return apiBaseIsAbsolute ? `${apiBase.origin}${trustedPath}` : trustedPath;
  } catch {
    throw new Error(INVALID_URL_MESSAGE);
  }
}
