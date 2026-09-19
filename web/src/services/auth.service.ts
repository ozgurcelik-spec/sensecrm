/**
 * Auth service - `/api/auth/*`. Anonymous calls are marked `skipAuthRetry` so a 401 (e.g. wrong
 * password) reaches the caller instead of triggering the refresh flow.
 */
import { apiClient } from "@/lib/api-client";
import type { AuthTokens, Locale } from "@/types";

export interface SignupRequest {
  organizationName: string;
  displayName: string;
  email: string;
  password: string;
  locale: Locale;
}

const AUTH_BASE = "/auth";

export async function login(email: string, password: string): Promise<AuthTokens> {
  const { data } = await apiClient.post<AuthTokens>(
    `${AUTH_BASE}/login`,
    { email, password },
    { skipAuthRetry: true }
  );
  return data;
}

export async function signup(request: SignupRequest): Promise<AuthTokens> {
  const { data } = await apiClient.post<AuthTokens>(`${AUTH_BASE}/signup`, request, {
    skipAuthRetry: true,
  });
  return data;
}

export async function refreshTokens(refreshToken: string): Promise<AuthTokens> {
  const { data } = await apiClient.post<AuthTokens>(
    `${AUTH_BASE}/refresh`,
    { refreshToken },
    { skipAuthRetry: true }
  );
  return data;
}

export async function logout(refreshToken: string): Promise<void> {
  await apiClient.post(`${AUTH_BASE}/logout`, { refreshToken }, { skipAuthRetry: true });
}

export async function switchOrganization(organizationId: string): Promise<AuthTokens> {
  const { data } = await apiClient.post<AuthTokens>(`${AUTH_BASE}/switch-organization`, {
    organizationId,
  });
  return data;
}

/** Anonymous client configuration (`GET /auth/config`); currently only whether public sign-up is open. */
export interface AuthConfig {
  signupEnabled: boolean;
}

export const authConfigKey = ["auth", "config"] as const;

export async function getAuthConfig(): Promise<AuthConfig> {
  const { data } = await apiClient.get<AuthConfig>(`${AUTH_BASE}/config`, { skipAuthRetry: true });
  return data;
}
