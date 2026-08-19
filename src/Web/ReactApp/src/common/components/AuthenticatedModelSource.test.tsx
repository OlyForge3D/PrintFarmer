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
  // Each call returns a *distinct* URL (like a real `URL.createObjectURL`
  // would for two different Blobs). Tests that resolve the same `url` value
  // more than once rely on this to distinguish a freshly-minted object URL
  // from an earlier, already-revoked one for that same source url — with a
  // constant mock value, a test could pass identically whether or not the
  // component actually re-resolved.
  let objectUrlCounter = 0;
  const createObjectURLSpy = vi.fn(() => `blob:mock-object-url-${objectUrlCounter++}`);
  const revokeObjectURLSpy = vi.fn();

  beforeEach(() => {
    apiClientGetMock.mockReset();
    objectUrlCounter = 0;
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

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url-0'));

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

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url-0'));

    unmount();

    expect(revokeObjectURLSpy).toHaveBeenCalledWith('blob:mock-object-url-0');
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

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url-0'));
    expect(onError).not.toHaveBeenCalled();
  });

  /**
   * Regression coverage for Bishop's round-2 stale-Blob-URL hazard: if `url`
   * changes away and then back before the new fetch resolves, children must
   * never be handed the earlier resolved (and possibly already-revoked) blob
   * URL for the old `url` value — the component must re-resolve instead.
   *
   * `createObjectURLSpy` returns a *distinct* value per call (see setup
   * above), so "children were called with model-a's object URL" only proves
   * the second (fresh) resolution if we also assert `apiClientGetMock` was
   * actually invoked a third time for model-a — otherwise a reverted
   * implementation that simply reuses the first, stale, already-revoked
   * object URL would satisfy an identical assertion.
   */
  it('re-resolves instead of reusing a stale blob URL when url changes away and back', async () => {
    let resolveSecondFetch: (value: { data: ArrayBuffer }) => void = () => {};
    apiClientGetMock.mockImplementationOnce(() => Promise.resolve({ data: new ArrayBuffer(4) }));
    const renderChild = vi.fn().mockReturnValue(<div data-testid="model" />);

    const { rerender } = render(
      <AuthenticatedModelSource url="/api/3d-models/file/model-a">{renderChild}</AuthenticatedModelSource>
    );

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url-0'));
    expect(apiClientGetMock).toHaveBeenCalledTimes(1);
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
    expect(apiClientGetMock).toHaveBeenCalledTimes(2);

    // Switch back to model-a before model-b's fetch resolves.
    apiClientGetMock.mockImplementationOnce(() => Promise.resolve({ data: new ArrayBuffer(4) }));
    rerender(
      <AuthenticatedModelSource url="/api/3d-models/file/model-a">{renderChild}</AuthenticatedModelSource>
    );

    // Confirm a genuinely fresh fetch was made for model-a (a stale-state bug
    // would instead reuse the cached first-call object URL without a third
    // apiClient call). createObjectURL is called only twice total: once for
    // the first model-a resolution, once for the second — model-b's fetch
    // never resolves before its effect is cleaned up (aborted), so it never
    // reaches createObjectURL.
    await waitFor(() => expect(apiClientGetMock).toHaveBeenCalledTimes(3));
    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url-1'));
    expect(renderChild).not.toHaveBeenCalledWith('blob:mock-object-url-0');

    // Resolve the abandoned model-b fetch afterward; its controller was
    // already aborted when we navigated away from it, so it must not call
    // createObjectURL or clobber the freshly-resolved model-a state.
    resolveSecondFetch({ data: new ArrayBuffer(4) });
    await waitFor(() => Promise.resolve());
    expect(createObjectURLSpy).toHaveBeenCalledTimes(2);
    expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url-1');
  });

  /**
   * Regression coverage for Hicks's and Vasquez's round-3 finding: the
   * stale-state reset must run on *every* url change, including a transition
   * through a non-authenticated url — not only between two authenticated
   * urls. Before this fix, the reset lived after the `!requiresAuthentication`
   * early return, so an authenticated A -> public B -> authenticated A
   * transition left A's revoked object URL in `loadedSource` and the second
   * visit to A rendered it without ever re-fetching.
   */
  it('re-resolves an authenticated url after transitioning through a non-authenticated url', async () => {
    apiClientGetMock.mockImplementationOnce(() => Promise.resolve({ data: new ArrayBuffer(4) }));
    const renderChild = vi.fn().mockReturnValue(<div data-testid="model" />);

    const { rerender } = render(
      <AuthenticatedModelSource url="/api/3d-models/file/model-a">{renderChild}</AuthenticatedModelSource>
    );

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url-0'));
    expect(apiClientGetMock).toHaveBeenCalledTimes(1);
    renderChild.mockClear();

    // Transition to a non-authenticated url (e.g. a bed texture) — this hits
    // the early-return branch of the effect.
    rerender(
      <AuthenticatedModelSource url="/textures/bed.png">{renderChild}</AuthenticatedModelSource>
    );

    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('/textures/bed.png'));
    renderChild.mockClear();

    // Transition back to the same authenticated url as before.
    apiClientGetMock.mockImplementationOnce(() => Promise.resolve({ data: new ArrayBuffer(4) }));
    rerender(
      <AuthenticatedModelSource url="/api/3d-models/file/model-a">{renderChild}</AuthenticatedModelSource>
    );

    // Must re-fetch (a second apiClient call) and hand children a fresh
    // object URL, not the first, already-revoked one.
    await waitFor(() => expect(apiClientGetMock).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(renderChild).toHaveBeenCalledWith('blob:mock-object-url-1'));
    expect(renderChild).not.toHaveBeenCalledWith('blob:mock-object-url-0');
  });
});

