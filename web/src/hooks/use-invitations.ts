import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  acceptInvitation,
  declineInvitation,
  invitationKeys,
  listInvitations,
} from "@/services/invitations.service";
import { useAuthStore } from "@/store/auth.store";

/** How often pending invitations are polled (only while the tab is visible), like the approvals bell. */
export const INVITATION_POLL_INTERVAL_MS = 60_000;

/** Pending invitations of the signed-in user: loaded on mount, polled every minute while visible. */
export function useInvitations(enabled = true) {
  return useQuery({
    queryKey: invitationKeys.all,
    queryFn: listInvitations,
    enabled,
    refetchInterval: INVITATION_POLL_INTERVAL_MS,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: true,
    staleTime: 0,
  });
}

/** Accepts an invitation, then reloads /me so the new organization shows up in the switcher. */
export function useAcceptInvitation() {
  const queryClient = useQueryClient();
  const refreshMe = useAuthStore((state) => state.refreshMe);
  return useMutation({
    mutationFn: (id: string) => acceptInvitation(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: invitationKeys.all });
      await refreshMe();
    },
  });
}

export function useDeclineInvitation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => declineInvitation(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: invitationKeys.all }),
  });
}
