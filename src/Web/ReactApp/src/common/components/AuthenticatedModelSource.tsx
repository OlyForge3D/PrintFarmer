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
}

export function AuthenticatedModelSource({
  url,
  children,
  renderLoading,
  renderError,
}: AuthenticatedModelSourceProps) {
  const requiresAuthentication = isAuthenticatedModelUrl(url);
  const [loadedSource, setLoadedSource] = useState<{ source: string; objectUrl: string } | null>(null);
  const [loadError, setLoadError] = useState<{ source: string; message: string } | null>(null);

  useEffect(() => {
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
          setLoadError({
            source: url,
            message: error instanceof Error ? error.message : 'Failed to load model',
          });
        }
      });

    return () => {
      controller.abort();
      if (objectUrl) {
        window.URL.revokeObjectURL(objectUrl);
      }
    };
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
