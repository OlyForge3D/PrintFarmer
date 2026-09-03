import { afterEach, describe, expect, it } from 'vitest';
import { resolveTrustedGcodeArtifactUrl } from '../gcode-artifact-url';

const ARTIFACT_ID = 'f47ac10b-58cc-4372-a567-0e02b2c3d479';
const hadOriginalApiBaseUrl = Object.prototype.hasOwnProperty.call(
  import.meta.env,
  'VITE_API_BASE_URL',
);
const originalApiBaseUrl = import.meta.env.VITE_API_BASE_URL;

afterEach(() => {
  if (hadOriginalApiBaseUrl) {
    import.meta.env.VITE_API_BASE_URL = originalApiBaseUrl;
  } else {
    delete import.meta.env.VITE_API_BASE_URL;
  }
});

describe('resolveTrustedGcodeArtifactUrl', () => {
  it('accepts the canonical same-origin artifact route', () => {
    delete import.meta.env.VITE_API_BASE_URL;

    expect(resolveTrustedGcodeArtifactUrl(`/api/artifacts/${ARTIFACT_ID}`))
      .toBe(`/api/artifacts/${ARTIFACT_ID}`);
  });

  it('accepts the exact configured absolute API route used by local development', () => {
    import.meta.env.VITE_API_BASE_URL = 'http://localhost:5245';

    expect(resolveTrustedGcodeArtifactUrl(`http://localhost:5245/api/artifacts/${ARTIFACT_ID}`))
      .toBe(`http://localhost:5245/api/artifacts/${ARTIFACT_ID}`);
  });

  it.each([
    `http://localhost:3000/api/artifacts/${ARTIFACT_ID}`,
    `https://evil.example/api/artifacts/${ARTIFACT_ID}`,
    `//evil.example/api/artifacts/${ARTIFACT_ID}`,
    `/api/artifacts/job/${ARTIFACT_ID}`,
    `/api/artifacts/${ARTIFACT_ID}/metadata`,
    `/api/artifacts/${ARTIFACT_ID}?download=true`,
    '/api/artifacts/not-a-guid',
    'blob:http://localhost:3000/local-gcode',
  ])('rejects untrusted or unexpected URL %s', (url) => {
    delete import.meta.env.VITE_API_BASE_URL;

    expect(() => resolveTrustedGcodeArtifactUrl(url))
      .toThrow('Invalid G-code artifact URL.');
  });
});
