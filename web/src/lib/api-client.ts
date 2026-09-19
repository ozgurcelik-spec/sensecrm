/**
 * Axios API client (mirrors senseik's `@senseik/api-client`): Bearer token from localStorage,
 * refresh-once-on-401 with a single in-flight refresh shared by concurrent requests, and a hard
 * redirect to /login when the session cannot be recovered. It knows nothing about the auth store -
 * the store wires itself in via `setRefreshHandler` (see lib/http-interceptors.ts).
 */
import axios, { type AxiosError, type AxiosInstance, type AxiosResponse } from "axios";

declare module "axios" {
  interface AxiosRequestConfig {
    /** Set on anonymous auth calls (login/signup/refresh/logout): their own 401 must never trigger a refresh. */
    skipAuthRetry?: boolean;
    /** Set on the retried copy of a request after a successful refresh, so a second 401 ends the session. */
    _retriedAfterRefresh?: boolean;
    /**
     * Keeps the Bearer header but lets a 401 reach the caller (no refresh, no session end): for
     * calls whose own 401 means "wrong current password", e.g. POST /me/password.
     */
    passthroughUnauthorized?: boolean;
  }
}

/**
 * All endpoints are URL-versioned under `/api/v1`. The same-origin fallback keeps dev (Vite proxy
 * `/api` -> backend) working and never sends the token over plain http.
 */
const baseURL = import.meta.env.VITE_API_BASE_URL || "/api/v1";

/** localStorage key holding the access token attached to every request. */
export const AUTH_TOKEN_STORAGE_KEY = "auth_token";
/** localStorage key holding the rotating refresh token. */
export const REFRESH_TOKEN_STORAGE_KEY = "refresh_token";
/** The persisted auth store (zustand `persist` name in store/auth.store.ts). */
export const AUTH_STORE_STORAGE_KEY = "auth-store";

/** localStorage access must not throw (private mode / disabled storage). */
export function readStorage(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function writeStorage(key: string, value: string | null): void {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    // Storage unavailable - the value lives in memory (auth store) for this tab only.
  }
}

export function clearSessionStorage(): void {
  writeStorage(AUTH_TOKEN_STORAGE_KEY, null);
  writeStorage(REFRESH_TOKEN_STORAGE_KEY, null);
  writeStorage(AUTH_STORE_STORAGE_KEY, null);
}

export type RefreshHandler = () => Promise<boolean>;
let refreshHandler: RefreshHandler | null = null;

/** Call once at startup: attempts a token refresh and reports whether it succeeded (never throws). */
export function setRefreshHandler(handler: RefreshHandler | null): void {
  refreshHandler = handler;
}

export type SessionExpiredHandler = () => void;
let sessionExpiredHandler: SessionExpiredHandler | null = null;

/** Notified right before the redirect to /login so the user can be told why. */
export function setSessionExpiredHandler(handler: SessionExpiredHandler | null): void {
  sessionExpiredHandler = handler;
}

/** ProblemDetails `code` of the 403 the API answers with while the user must change the password first. */
export const PASSWORD_CHANGE_REQUIRED_CODE = "auth.password_change_required";

export type PasswordChangeRequiredHandler = () => void;
let passwordChangeRequiredHandler: PasswordChangeRequiredHandler | null = null;

/** Called on any 403 `auth.password_change_required`; the app then routes to the forced change screen. */
export function setPasswordChangeRequiredHandler(
  handler: PasswordChangeRequiredHandler | null
): void {
  passwordChangeRequiredHandler = handler;
}

/** Several requests can 401 at once when the access token expires; the refresh token rotates, so refresh only once. */
let refreshInFlight: Promise<boolean> | null = null;

function refreshOnce(): Promise<boolean> {
  if (!refreshHandler) return Promise.resolve(false);
  if (!refreshInFlight) {
    refreshInFlight = refreshHandler()
      .catch(() => false)
      .finally(() => {
        refreshInFlight = null;
      });
  }
  return refreshInFlight;
}

/** Ends the session once even if several in-flight requests 401 together; the page reload resets the flag. */
let handlingSessionExpiry = false;

function handleSessionExpired(): void {
  if (handlingSessionExpiry) return;
  handlingSessionExpiry = true;
  clearSessionStorage();
  sessionExpiredHandler?.();
  // Fixed same-origin path - never a caller-supplied URL, so no open redirect.
  window.location.assign("/login");
}

const apiClient: AxiosInstance = axios.create({
  baseURL,
  timeout: 30_000,
  headers: { "Content-Type": "application/json" },
});

apiClient.interceptors.request.use((config) => {
  // Bearer token is never sent via query params - header only.
  const token = readStorage(AUTH_TOKEN_STORAGE_KEY);
  if (token && !config.skipAuthRetry) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

function isPasswordChangeRequired(error: AxiosError): boolean {
  const data = error.response?.data;
  return (
    !!data &&
    typeof data === "object" &&
    (data as { code?: unknown }).code === PASSWORD_CHANGE_REQUIRED_CODE
  );
}

/**
 * On a 401 from an authenticated request: refresh once (deduped) and retry the request once; end
 * the session if the refresh fails or the retry 401s again. Exported for unit tests.
 */
export async function handleResponseError(error: AxiosError): Promise<AxiosResponse> {
  const config = error.config;
  const hadAuthHeader = !!config?.headers?.Authorization;
  if (error.response?.status === 403 && isPasswordChangeRequired(error)) {
    passwordChangeRequiredHandler?.();
    return Promise.reject(error);
  }
  if (
    error.response?.status !== 401 ||
    !config ||
    !hadAuthHeader ||
    config.skipAuthRetry ||
    config.passthroughUnauthorized
  ) {
    return Promise.reject(error);
  }

  if (!config._retriedAfterRefresh) {
    const refreshed = await refreshOnce();
    if (refreshed) {
      // The request interceptor re-reads the now-refreshed token from storage.
      return apiClient.request({ ...config, _retriedAfterRefresh: true });
    }
  }

  handleSessionExpired();
  return Promise.reject(error);
}

apiClient.interceptors.response.use((response) => response, handleResponseError);

export { apiClient };
export default apiClient;
