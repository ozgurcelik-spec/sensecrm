import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  contactKeys,
  createContact,
  deleteContact,
  getContact,
  listContacts,
  updateContact,
  type ContactListQuery,
} from "@/services/contacts.service";
import type { ContactInput } from "@/types";

export function useContacts(query: ContactListQuery, enabled = true) {
  return useQuery({
    queryKey: contactKeys.list(query),
    queryFn: () => listContacts(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useContact(id: string | undefined) {
  return useQuery({
    queryKey: contactKeys.detail(id ?? ""),
    queryFn: () => getContact(id as string),
    enabled: !!id,
  });
}

export function useSaveContact() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, ...input }: ContactInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateContact(id, input);
        return id;
      }
      return (await createContact(input)).id;
    },
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: contactKeys.all }),
        // Account detail shows contact counts and the related contacts tab.
        queryClient.invalidateQueries({ queryKey: ["accounts"] }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}

export function useDeleteContact() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => deleteContact(id),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: contactKeys.all }),
        queryClient.invalidateQueries({ queryKey: ["accounts"] }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}
