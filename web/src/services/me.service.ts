/**
 * Current user ("me") service - `GET/PATCH /api/me`.
 */
import { apiClient } from "@/lib/api-client";
import type { Locale, Me } from "@/types";

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
