import { useEffect, useRef, useState } from 'react';
import { getAuthenticatedCameraProxyRoute } from '@/common/auth/authenticatedCameraRoutes';
import { clearStoredAuthentication, renewAccessToken } from '@/common/auth/sessionTokens';
import { resetAuthenticatedSignalRSession } from '@/common/auth/authenticatedSignalRSession';
import { notifyAuthenticationExpired } from '@/common/auth/authenticationExpiration';
import { getMjpegBoundary, MjpegParser } from '@/features/cameras/utils/mjpegParser';

const INITIAL_RECONNECT_DELAY_MS = 1_000;
const MAX_RECONNECT_DELAY_MS = 30_000;
const MAX_SINGLE_IMAGE_BYTES = 8 * 1024 * 1024;

interface StreamState {
  route: string;
  src: string | null;
  unsupported: boolean;
  failed: boolean;
}

function createObjectUrl(blob: Blob): string {
  return URL.createObjectURL(blob);
}

export function useAuthenticatedMjpegStream(streamUrl?: string | null, enabled = true) {
  const [state, setState] = useState<StreamState>({ route: '', src: null, unsupported: false, failed: false });
  const currentUrlRef = useRef<string | null>(null);
  const [authRevision, setAuthRevision] = useState(0);
  const route = getAuthenticatedCameraProxyRoute(streamUrl);

  useEffect(() => {
    const onStorage = (event: StorageEvent) => {
      if (event.key === 'auth-token' || event.key === 'auth-user-id') setAuthRevision((revision) => revision + 1);
    };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, []);

  useEffect(() => {
    const revoke = () => {
      if (currentUrlRef.current) URL.revokeObjectURL(currentUrlRef.current);
      currentUrlRef.current = null;
    };
    if (!route || !enabled) {
      revoke();
      setState({ route: route ?? '', src: null, unsupported: false, failed: false });
      return;
    }
    const currentRoute = route;

    let cancelled = false;
    let controller: AbortController | undefined;
    let reconnectTimer: number | undefined;
    let reconnectDelay = INITIAL_RECONNECT_DELAY_MS;
    let lastFrameAt = 0;

    const publishFrame = (bytes: Uint8Array, contentType = 'image/jpeg') => {
      if (cancelled || Date.now() - lastFrameAt < 100) return;
      lastFrameAt = Date.now();
      const url = createObjectUrl(new Blob([new Uint8Array(bytes).buffer as ArrayBuffer], { type: contentType }));
      revoke();
      currentUrlRef.current = url;
      setState({ route: currentRoute, src: url, unsupported: false, failed: false });
    };

    const reconnect = (failed: boolean) => {
      if (cancelled) return;
      if (failed) setState((current) => ({ ...current, route: currentRoute, failed: true }));
      reconnectTimer = window.setTimeout(() => { void connect(); }, reconnectDelay);
      reconnectDelay = Math.min(MAX_RECONNECT_DELAY_MS, reconnectDelay * 2);
    };

    const connect = async () => {
      controller = new AbortController();
      try {
        let token = localStorage.getItem('auth-token');
        const tokenAtRequest = token;
        let response = await fetch(currentRoute, {
          headers: token ? { Authorization: `Bearer ${token}` } : {},
          signal: controller.signal,
        });
        if (response.status === 401) {
          token = await renewAccessToken();
          if (!token && localStorage.getItem('auth-token') !== tokenAtRequest) token = localStorage.getItem('auth-token');
          if (!token || cancelled) {
            if (!cancelled) {
              await resetAuthenticatedSignalRSession().catch(() => undefined);
              clearStoredAuthentication();
              notifyAuthenticationExpired();
              if (window.location.pathname !== '/login' && window.location.pathname !== '/register') window.location.href = '/login';
            }
            return;
          }
          response = await fetch(currentRoute, { headers: { Authorization: `Bearer ${token}` }, signal: controller.signal });
          if (response.status === 401) {
            await resetAuthenticatedSignalRSession().catch(() => undefined);
            clearStoredAuthentication();
            notifyAuthenticationExpired();
            if (window.location.pathname !== '/login' && window.location.pathname !== '/register') window.location.href = '/login';
            return;
          }
        }
        if (!response.ok || !response.body) throw new Error(`Camera stream returned HTTP ${response.status}`);

        const contentType = response.headers.get('content-type');
        const boundary = getMjpegBoundary(contentType);
        const connectedAt = Date.now();
        const reader = response.body.getReader();
        if (!boundary) {
          if (contentType?.toLowerCase().startsWith('image/jpeg')) {
            const parts: Uint8Array[] = [];
            let byteCount = 0;
            while (!cancelled) {
              const result = await reader.read();
              if (result.done) break;
              byteCount += result.value.length;
              if (byteCount > MAX_SINGLE_IMAGE_BYTES) break;
              parts.push(result.value);
            }
            if (byteCount > 0 && byteCount <= MAX_SINGLE_IMAGE_BYTES) {
              const image = new Uint8Array(byteCount);
              let offset = 0;
              for (const part of parts) { image.set(part, offset); offset += part.length; }
              publishFrame(image);
              reconnect(false);
              return;
            }
          }
          await reader.cancel();
          setState({ route: currentRoute, src: null, unsupported: true, failed: false });
          return;
        }

        const parser = new MjpegParser(boundary);
        while (!cancelled) {
          const result = await reader.read();
          if (result.done) break;
          for (const frame of parser.push(result.value)) publishFrame(frame);
          if (parser.didResetAfterOverflow()) setState((current) => ({ ...current, route: currentRoute, failed: true }));
        }
        if (Date.now() - connectedAt >= 30_000) reconnectDelay = INITIAL_RECONNECT_DELAY_MS;
        reconnect(false);
      } catch {
        if (!cancelled && !controller?.signal.aborted) reconnect(true);
      }
    };

    setState({ route: currentRoute, src: null, unsupported: false, failed: false });
    void connect();
    return () => {
      cancelled = true;
      controller?.abort();
      if (reconnectTimer !== undefined) window.clearTimeout(reconnectTimer);
      revoke();
    };
  }, [authRevision, enabled, route]);

  return {
    streamSrc: state.route === route ? state.src : null,
    streamUnsupported: state.route === route && state.unsupported,
    streamFailed: state.route === route && state.failed,
  };
}
