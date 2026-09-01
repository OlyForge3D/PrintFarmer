// Shared axios instance + interceptors used by every per-domain API module.
//
// This module intentionally has no dependency on `services/api.ts` (the legacy
// `ApiClient` monolith) so that domain modules importing only this file stay out
// of that monolith's module graph and remain tree-shakeable per route. See
// issue #2343.
import type { AxiosError, AxiosInstance, AxiosRequestConfig } from "axios";
import axios from "axios";
import { getApiBaseUrl } from "@/common/utils/apiUrlHelpers";
import { extractValidationErrorMessage } from "@/common/utils/apiErrors";
import { resetAuthenticatedSignalRSession } from "@/common/auth/authenticatedSignalRSession";
import { notifyAuthenticationExpired } from "@/common/auth/authenticationExpiration";
import type { ApiError } from "@/types/api";

/**
 * Extended Axios request config with PrintFarmer-specific interceptor bypass flags.
 * Pass a `PfRequestConfig` when you need to suppress the default 401 redirect
 * behaviour for endpoints that signal soft failures via 401 (e.g. passkey
 * assertion completion).
 */
export interface PfRequestConfig extends AxiosRequestConfig {
  /**
   * When `true`, a 401 response will not trigger the global token-clear and
   * redirect-to-/login behaviour in the response interceptor. Use this for
   * endpoints that legitimately return 401 to indicate a failed operation
   * rather than an expired session.
   */
  skipAuthRedirect?: boolean;
}

interface PfInternalRequestConfig extends PfRequestConfig {
  authTokenAtRequest?: string | null;
}

// Utility to generate a correlation ID (UUID v4)
function generateCorrelationId(): string {
  // Use crypto API if available, fallback to random
  if (typeof crypto !== "undefined" && crypto.randomUUID) {
    return crypto.randomUUID();
  }
  // Fallback: simple random string
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(
    /[xy]/g,
    function (c) {
      const r = (Math.random() * 16) | 0,
        v = c === "x" ? r : (r & 0x3) | 0x8;
      return v.toString(16);
    }
  );
}

function createHttpClient(): AxiosInstance {
  // Use shared utility to properly construct API base URL
  const apiBaseUrl = getApiBaseUrl();

  const instance = axios.create({
    baseURL: apiBaseUrl,
    timeout: 30000,
    paramsSerializer: {
      // ASP.NET Core expects repeated keys for arrays: tagIds=a&tagIds=b
      // Axios v1+ defaults to bracket notation (tagIds[]=a) which .NET ignores
      indexes: null,
    },
  });

  // Request interceptor for authentication and correlationId
  instance.interceptors.request.use((config) => {
    const token = localStorage.getItem("auth-token");
    (config as PfInternalRequestConfig).authTokenAtRequest = token;
    if (token) {
      config.headers.Authorization = `Bearer ${token}`;
    }
    // Add correlationId header to every request
    config.headers["X-Correlation-Id"] = generateCorrelationId();

    // Set Content-Type for non-FormData requests
    // FormData has its own Content-Type with boundary, so we let the browser/axios handle it
    if (!(config.data instanceof FormData)) {
      config.headers["Content-Type"] = "application/json";
    }

    return config;
  });

  // Response interceptor for error handling
  instance.interceptors.response.use(
    (response) => response,
    async (error: AxiosError) => {
      const requestConfig = error.config as PfInternalRequestConfig | undefined;
      // Handle 401 Unauthorized — clear token and redirect to login unless
      // the caller set skipAuthRedirect:true on the request config to handle
      // the 401 inline (e.g. passkey assertion, which the backend signals
      // with 401 for failed credentials rather than as a session expiry).
      if (
        error.response?.status === 401 &&
        !requestConfig?.skipAuthRedirect &&
        requestConfig?.authTokenAtRequest === localStorage.getItem("auth-token")
      ) {
        let invalidatedCurrentSession = false;
        try {
          await resetAuthenticatedSignalRSession();
        } catch (resetError) {
          console.error(
            "Failed to reset authenticated SignalR session after a 401 response.",
            resetError,
          );
        }
        if (requestConfig.authTokenAtRequest === localStorage.getItem("auth-token")) {
          localStorage.removeItem("auth-token");
          notifyAuthenticationExpired();
          invalidatedCurrentSession = true;
        }
        // Only redirect if not already on auth pages
        if (
          invalidatedCurrentSession &&
          window.location.pathname !== "/login" &&
          window.location.pathname !== "/register"
        ) {
          window.location.href = "/login";
        }
      }

      const responseData = error.response?.data;

      // Legacy string-shape `details`: keep this behavior for existing
      // callers. Only surface a string when the body itself is a string or
      // carries `{ error: string }`. Never stringify objects into `details`.
      const detailMessage = typeof responseData === 'string'
        ? responseData
        : (responseData as { error?: string })?.error ?? undefined;

      // Prefer a ProblemDetails-style top-level message from the body:
      // backend emits `{ message: "..." }` for some endpoints and
      // `{ detail: "..." }` for `application/problem+json`. Fall back to the
      // axios error message only when neither exists. Preserve the raw body
      // and the axios-error flag so feature callers (e.g. partsHarvest,
      // partsInventory) can recover canonical `code`/`mismatches`/`details`
      // extensions instead of collapsing every failure into an opaque error.
      const bodyRecord =
        responseData && typeof responseData === 'object'
          ? (responseData as { message?: unknown; detail?: unknown })
          : undefined;
      const bodyMessage =
        typeof bodyRecord?.message === 'string' && bodyRecord.message.length > 0
          ? bodyRecord.message
          : typeof bodyRecord?.detail === 'string' && bodyRecord.detail.length > 0
            ? bodyRecord.detail
            : undefined;

      // ASP.NET Core model-binding failures (e.g. a malformed request body
      // field, such as a non-GUID string sent for a `Guid?` property) return
      // a `ValidationProblemDetails`-shaped `errors` dictionary with no
      // top-level `message`/`detail` — surface that detail instead of
      // falling through to the generic axios error message (issue #1973).
      const validationMessage = extractValidationErrorMessage(responseData);

      const apiError: ApiError = {
        message: bodyMessage ?? (detailMessage || validationMessage || error.message),
        statusCode: error.response?.status || 500,
        details: detailMessage ?? validationMessage,
        data: responseData,
        isAxiosError: axios.isAxiosError(error),
      };
      return Promise.reject(apiError);
    }
  );

  return instance;
}

/** Shared axios instance used by every per-domain API module. */
export const client: AxiosInstance = createHttpClient();

/**
 * Generic typed request helper, mirroring the legacy `ApiClient.request`
 * signature/behavior exactly, for domain modules that need arbitrary
 * method/url/config combinations rather than a dedicated wrapper. See
 * issue #2343.
 */
export async function request<T>(config: PfRequestConfig): Promise<T> {
  const response = await client.request<T>(config);
  return response.data;
}
