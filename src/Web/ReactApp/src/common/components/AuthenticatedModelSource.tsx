/**
 * Resolves an (optionally authenticated) 3D model URL to a URL that any
 * three.js loader can fetch without carrying auth headers.
 *
 * For authenticated API URLs (see `isAuthenticatedModelUrl`), the bytes are
 * pre-fetched through `apiClient` (bearer token attached) and exposed as a
 * `Blob` object URL. For any other URL, the original URL is passed through
 * unchanged.
 *
 * Used to fix #1711: three.js loaders (STLLoader/PLYLoader/etc.) issue a bare
 * fetch/XHR with no Authorization header, so pointing them directly at
 * `/api/3d-models/file/{id}` returns 401.
 */
import React, { useEffect, useState } from 'react';
import { Html } from '@react-three/drei';
import { isAuthenticatedModelUrl, loadModelArrayBuffer } from '@/common/utils/authenticatedModelUrl';

export interface AuthenticatedModelSourceProps {
  url: string;
  children: (resolvedUrl: string) => React.ReactNode;
  /** Rendered while an authenticated fetch is in flight. Defaults to a small in-canvas HTML overlay. */
  renderLoading?: () => React.ReactNode;
  /** Rendered when the authenticated fetch fails. Defaults to a small in-canvas HTML overlay. */
  renderError?: (message: string) => React.ReactNode;
  /**
   * Called (in addition to the default/`renderError` UI) when the authenticated
   * fetch fails. Lets callers that sit inside an error-boundary-driven failure
   * pipeline (e.g. SlicerBedVisualization's `onModelLoadError`, which used to
   * fire from a thrown `useLoader` error) observe the failure too — this
   * component never throws, so without this callback that signal would
   * otherwise be silently lost. See #1711.
   */
  onError?: (message: string) => void;
}

export function AuthenticatedModelSource({
  url,
  children,
  renderLoading,
  renderError,
  onError,
}: AuthenticatedModelSourceProps) {
  const requiresAuthentication = isAuthenticatedModelUrl(url);
  const [loadedSource, setLoadedSource] = useState<{ source: string; objectUrl: string } | null>(null);
  const [loadError, setLoadError] = useState<{ source: string; message: string } | null>(null);

  useEffect(() => {
    // Reset stale state from a previous `url` immediately, before the
    // `requiresAuthentication` early return below. Without this, transitioning
    // authenticated URL A -> a non-authenticated URL B -> back to A (before a
    // fresh authenticated fetch for A completes) would leave `loadedSource`
    // holding A's *already-revoked* object URL (revoked by the effect cleanup
    // when leaving A), and the render guard below would match
    // `loadedSource.source === url` against that stale, revoked value instead
    // of re-resolving it. The same hazard applies to an authenticated A -> B
    // -> A transition where B is also authenticated.
    setLoadedSource(null);
    setLoadError(null);

    if (!requiresAuthentication) {
      return;
    }

    const controller = new AbortController();
    let objectUrl: string | null = null;

    void loadModelArrayBuffer(url, controller.signal)
      .then((data) => {
        if (controller.signal.aborted) {
          return;
        }

        objectUrl = window.URL.createObjectURL(new Blob([data]));
        setLoadError(null);
        setLoadedSource({ source: url, objectUrl });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          const message = error instanceof Error ? error.message : 'Failed to load model';
          setLoadError({ source: url, message });
          onError?.(message);
        }
      });

    return () => {
      controller.abort();
      if (objectUrl) {
        window.URL.revokeObjectURL(objectUrl);
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- onError is intentionally excluded: it is expected to be an inline callback at most call sites and including it would re-run the fetch on every render.
  }, [requiresAuthentication, url]);

  if (!requiresAuthentication) {
    return <>{children(url)}</>;
  }

  if (loadError?.source === url) {
    const message = loadError.message;
    return (
      <>
        {renderError ? renderError(message) : (
          <Html center>
            <div className="max-w-xs rounded-lg border border-red-500/40 bg-pf-bg-1/95 px-3 py-2 text-xs text-red-400 shadow-lg backdrop-blur-sm">
              {message}
            </div>
          </Html>
        )}
      </>
    );
  }

  if (loadedSource?.source !== url) {
    return (
      <>
        {renderLoading ? renderLoading() : (
          <Html center>
            <div className="rounded-lg border border-pf-border bg-pf-bg-2/90 px-4 py-2 text-sm text-pf-text-primary shadow-lg backdrop-blur-sm">
              Loading model...
            </div>
          </Html>
        )}
      </>
    );
  }

  return <>{children(loadedSource.objectUrl)}</>;
}
