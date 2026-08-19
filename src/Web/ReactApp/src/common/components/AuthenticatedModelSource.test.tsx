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

  /**
   * Regression coverage for Bishop's round-2 finding: `AuthenticatedModelSource`
   * swallows load failures into its own in-canvas error UI, so
   * `ModelViewerErrorBoundary` (the sole producer of `onModelLoadError` /
   * `failedModelLoadIds`, the #1709 guard that disables Slice on a failed
   * model) never fires for an authenticated model. The optional `onError`
   * callback is the bridge that lets `SlicerBedVisualization` re-wire that
   * signal without forcing every other caller to handle a thrown error.
   */
  it('calls onError when the authenticated fetch fails, without breaking the in-canvas error UI', async () => {
    apiClientGetMock.mockRejectedValue(new Error('Network error'));
    const renderChild = vi.fn().mockReturnValue(<div data-testid="model" />);
    const onError = vi.fn();

    render(
      <AuthenticatedModelSource url="/api/3d-models/file/model-123" onError={onError}>
        {renderChild}
      </AuthenticatedModelSource>
    );

    await waitFor(() => expect(onError).toHaveBeenCalledWith('Network error'));
    expect(screen.getByText('Network error')).toBeInTheDocument();
    expect(renderChild).not.toHaveBeenCalled();
  });

  it('does not call onError when the authenticated fetch succeeds', async () => {
    apiClientGetMock.mockResolvedValue({ data: new ArrayBuffer(4) });
    const renderChild = vi.fn().mockReturnValue(<div data-testid="model" />);
    const onError = vi.fn();

    render(
      <AuthenticatedModelSource url="/api/3d-models/file/model-123" onError={onError}>
        {renderChild}
      </AuthenticatedModelSource>
    );

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url'));
    expect(onError).not.toHaveBeenCalled();
  });

  /**
   * Regression coverage for Bishop's round-2 stale-Blob-URL hazard: if `url`
   * changes away and then back before the new fetch resolves, children must
   * never be handed the earlier resolved (and possibly already-revoked) blob
   * URL for the old `url` value — the component must re-resolve instead.
   */
  it('re-resolves instead of reusing a stale blob URL when url changes away and back', async () => {
    let resolveSecondFetch: (value: { data: ArrayBuffer }) => void = () => {};
    apiClientGetMock.mockImplementationOnce(() => Promise.resolve({ data: new ArrayBuffer(4) }));
    const renderChild = vi.fn().mockReturnValue(<div data-testid="model" />);

    const { rerender } = render(
      <AuthenticatedModelSource url="/api/3d-models/file/model-a">{renderChild}</AuthenticatedModelSource>
    );

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url'));
    renderChild.mockClear();

    // Switch to a different url whose fetch never resolves within this test...
    apiClientGetMock.mockImplementationOnce(() => new Promise((resolve) => {
      resolveSecondFetch = resolve;
    }));
    rerender(
      <AuthenticatedModelSource url="/api/3d-models/file/model-b">{renderChild}</AuthenticatedModelSource>
    );

    // ...children must not be re-invoked with the stale model-a blob URL
    // while model-b's fetch is still in flight.
    expect(renderChild).not.toHaveBeenCalled();

    // Switch back to model-a before model-b's fetch resolves.
    apiClientGetMock.mockImplementationOnce(() => Promise.resolve({ data: new ArrayBuffer(4) }));
    rerender(
      <AuthenticatedModelSource url="/api/3d-models/file/model-a">{renderChild}</AuthenticatedModelSource>
    );

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url'));

    // Resolve the abandoned model-b fetch afterward; it must not clobber
    // the freshly-resolved model-a state.
    resolveSecondFetch({ data: new ArrayBuffer(4) });
    expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url');
  });
});
