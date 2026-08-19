import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';

const { apiClientGetMock } = vi.hoisted(() => ({
  apiClientGetMock: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    get: apiClientGetMock,
  },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: vi.fn(() => '/api'),
}));

vi.mock('@react-three/drei', () => ({
  Html: ({ children }: { children: React.ReactNode }) => <div data-testid="html-overlay">{children}</div>,
}));

import { AuthenticatedModelSource } from './AuthenticatedModelSource';

/**
 * Regression coverage for #1711: this component is the single place that
 * resolves an authenticated `/api/3d-models/file/{id}` URL into a Blob object
 * URL (via `apiClient`, bearer token attached) before three.js loaders touch
 * it. Every viewer that renders model geometry from a URL (ModelViewer3D,
 * SlicerBedVisualization's STLModel, ThreeMFViewer's FallbackStlModel) MUST
 * wrap its `useLoader(...)` call in this component — these tests exist so
 * that a future refactor which accidentally bypasses this wrapper (reverting
 * to a bare `useLoader(STLLoader, rawAuthenticatedUrl)`) fails loudly instead
 * of silently reintroducing the 401.
 */
describe('AuthenticatedModelSource', () => {
  const createObjectURLSpy = vi.fn(() => 'blob:mock-object-url');
  const revokeObjectURLSpy = vi.fn();

  beforeEach(() => {
    apiClientGetMock.mockReset();
    createObjectURLSpy.mockClear();
    revokeObjectURLSpy.mockClear();
    vi.stubGlobal('URL', Object.assign(URL, {
      createObjectURL: createObjectURLSpy,
      revokeObjectURL: revokeObjectURLSpy,
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('passes a non-authenticated URL straight through without fetching', () => {
    const renderChild = vi.fn().mockReturnValue(<div data-testid="model" />);

    render(<AuthenticatedModelSource url="/textures/bed.png">{renderChild}</AuthenticatedModelSource>);

    expect(apiClientGetMock).not.toHaveBeenCalled();
    expect(renderChild).toHaveBeenCalledWith('/textures/bed.png');
    expect(screen.getByTestId('model')).toBeInTheDocument();
  });

  it('pre-fetches an authenticated URL through apiClient and hands children a blob object URL', async () => {
    apiClientGetMock.mockResolvedValue({ data: new ArrayBuffer(4) });
    const renderChild = vi.fn().mockReturnValue(<div data-testid="model" />);

    render(
      <AuthenticatedModelSource url="/api/3d-models/file/model-123">{renderChild}</AuthenticatedModelSource>
    );

    // While the fetch is in flight, the raw authenticated URL must never
    // reach the loader — this is exactly what caused the 401 in #1711.
    expect(renderChild).not.toHaveBeenCalled();

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url'));

    expect(apiClientGetMock).toHaveBeenCalledWith(
      '/api/3d-models/file/model-123',
      expect.objectContaining({ responseType: 'arraybuffer', baseURL: '' }),
    );
    expect(renderChild).not.toHaveBeenCalledWith('/api/3d-models/file/model-123');
  });

  it('revokes the object URL on unmount', async () => {
    apiClientGetMock.mockResolvedValue({ data: new ArrayBuffer(4) });
    const renderChild = vi.fn().mockReturnValue(<div data-testid="model" />);

    const { unmount } = render(
      <AuthenticatedModelSource url="/api/3d-models/file/model-123">{renderChild}</AuthenticatedModelSource>
    );

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url'));

    unmount();

    expect(revokeObjectURLSpy).toHaveBeenCalledWith('blob:mock-object-url');
  });

  it('surfaces a load failure instead of falling back to the raw authenticated URL', async () => {
    apiClientGetMock.mockRejectedValue(new Error('Network error'));
    const renderChild = vi.fn().mockReturnValue(<div data-testid="model" />);

    render(
      <AuthenticatedModelSource url="/api/3d-models/file/model-123">{renderChild}</AuthenticatedModelSource>
    );

    await waitFor(() => expect(screen.getByText('Network error')).toBeInTheDocument());
    expect(renderChild).not.toHaveBeenCalled();
  });
});
