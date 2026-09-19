/**
 * The signed-in user's organization invitations - `/api/v1/me/invitations` (C-SEC). Allowed even
 * while the user must still change the password.
 */
import { apiClient } from "@/lib/api-client";
import type { Invitation } from "@/types";

export const invitationKeys = {
  all: ["me", "invitations"] as const,
};

export async function listInvitations(): Promise<Invitation[]> {
  const { data } = await apiClient.get<Invitation[]>("/me/invitations");
  return data;
}

export async function acceptInvitation(id: string): Promise<void> {
  await apiClient.post(`/me/invitations/${encodeURIComponent(id)}/accept`);
}

export async function declineInvitation(id: string): Promise<void> {
  await apiClient.post(`/me/invitations/${encodeURIComponent(id)}/decline`);
}
