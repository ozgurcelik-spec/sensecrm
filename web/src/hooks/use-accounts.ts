import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  accountKeys,
  createAccount,
  deleteAccount,
  getAccount,
  listAccountContacts,
  listAccountDeals,
  listAccounts,
  updateAccount,
  type AccountListQuery,
} from "@/services/accounts.service";
import type { AccountInput } from "@/types";

export function useAccounts(query: AccountListQuery, enabled = true) {
  return useQuery({
    queryKey: accountKeys.list(query),
    queryFn: () => listAccounts(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useAccount(id: string | undefined) {
  return useQuery({
    queryKey: accountKeys.detail(id ?? ""),
    queryFn: () => getAccount(id as string),
    enabled: !!id,
  });
}

export function useAccountContacts(id: string | undefined) {
  return useQuery({
    queryKey: accountKeys.contacts(id ?? ""),
    queryFn: () => listAccountContacts(id as string),
    enabled: !!id,
  });
}

export function useAccountDeals(id: string | undefined) {
  return useQuery({
    queryKey: accountKeys.deals(id ?? ""),
    queryFn: () => listAccountDeals(id as string),
    enabled: !!id,
  });
}

/** Create (no `id`) or update an account. Resolves with the created account's id when creating. */
export function useSaveAccount() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, ...input }: AccountInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateAccount(id, input);
        return id;
      }
      return (await createAccount(input)).id;
    },
    onSuccess: async () => {
      // Contact/deal rows show the account name, and every change lands in the audit trail.
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: accountKeys.all }),
        queryClient.invalidateQueries({ queryKey: ["contacts"] }),
        queryClient.invalidateQueries({ queryKey: ["deals"] }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}

export function useDeleteAccount() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => deleteAccount(id),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: accountKeys.all }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}
