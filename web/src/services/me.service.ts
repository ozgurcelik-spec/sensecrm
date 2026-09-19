/**
 * Current user ("me") service - `GET/PATCH /api/me`.
 */
import { apiClient } from "@/lib/api-client";
import type { AuthTokens, Locale, Me } from "@/types";

export const ME_PATH = "/me";

export async function getMe(): Promise<Me> {
  const { data } = await apiClient.get<Me>(ME_PATH);
  return data;
}

export interface UpdateMeRequest {
  displayName?: string;
  locale?: Locale;
}

export async function updateMe(request: UpdateMeRequest): Promise<void> {
  await apiClient.patch(ME_PATH, request);
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

/**
 * `POST /me/password` -> new token pair (all other sessions are revoked server-side). A 401 here
 * means a wrong current password, so it must not end the session (`passthroughUnauthorized`).
 */
export async function changePassword(request: ChangePasswordRequest): Promise<AuthTokens> {
  const { data } = await apiClient.post<AuthTokens>(`${ME_PATH}/password`, request, {
    passthroughUnauthorized: true,
  });
  return data;
}
