import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createApiKey,
  createWebhook,
  deleteApiKey,
  deleteWebhook,
  getApiKeyUsage,
  getIntegrationsStatus,
  getWebhookDelivery,
  integrationKeys,
  listApiKeys,
  listWebhookDeliveries,
  listWebhookEvents,
  listWebhooks,
  redeliverWebhookDelivery,
  revokeApiKey,
  rotateWebhookSecret,
  setWebhookEnabled,
  testWebhook,
  updateApiKey,
  updateWebhook,
} from "@/services/integrations.service";
import type {
  ApiKeyInput,
  ApiKeyListQuery,
  ApiKeyPatch,
  WebhookDeliveryQuery,
  WebhookInput,
  WebhookListQuery,
} from "@/types";
import { DELIVERY_FINAL_STATUSES } from "@/lib/integrations";

/** `GET /integrations/status`: egress flag, host restriction, limits and live usage. */
export function useIntegrationsStatus(enabled = true) {
  return useQuery({ queryKey: integrationKeys.status, queryFn: getIntegrationsStatus, enabled });
}

/** Event catalog (static per plan): the picker and the developer guide. */
export function useWebhookEvents(enabled = true) {
  return useQuery({
    queryKey: integrationKeys.events,
    queryFn: listWebhookEvents,
    enabled,
    staleTime: 5 * 60_000,
  });
}

export function useWebhooks(query: WebhookListQuery, enabled = true) {
  return useQuery({
    queryKey: integrationKeys.webhookList(query),
    queryFn: () => listWebhooks(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

/** All subscriptions (up to 100) for the delivery log's subscription filter. */
export function useWebhookOptions(enabled = true) {
  return useQuery({
    queryKey: integrationKeys.webhookOptions,
    queryFn: () => listWebhooks({ page: 1, pageSize: 100, sort: "name" }),
    enabled,
  });
}

/**
 * The create / rotate / key-create answers carry a raw secret: `gcTime: 0` drops the mutation (and
 * its `data`) as soon as nobody observes it, so the secret lives only in the dialog that shows it.
 */
const SECRET_MUTATION = { gcTime: 0 } as const;

export function useCreateWebhook() {
  const queryClient = useQueryClient();
  return useMutation({
    ...SECRET_MUTATION,
    mutationFn: (input: WebhookInput) => createWebhook(input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: integrationKeys.webhooks });
      void queryClient.invalidateQueries({ queryKey: integrationKeys.status });
    },
  });
}

export function useUpdateWebhook() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: WebhookInput }) => updateWebhook(id, input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: integrationKeys.webhooks }),
  });
}

export function useSetWebhookEnabled() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) => setWebhookEnabled(id, enabled),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: integrationKeys.webhooks }),
  });
}

export function useDeleteWebhook() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => deleteWebhook(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: integrationKeys.webhooks });
      void queryClient.invalidateQueries({ queryKey: integrationKeys.deliveries });
      void queryClient.invalidateQueries({ queryKey: integrationKeys.status });
    },
  });
}

export function useRotateWebhookSecret() {
  const queryClient = useQueryClient();
  return useMutation({
    ...SECRET_MUTATION,
    mutationFn: ({ id, graceHours }: { id: string; graceHours: number }) => rotateWebhookSecret(id, graceHours),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: integrationKeys.webhooks }),
  });
}

export function useTestWebhook() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => testWebhook(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: integrationKeys.deliveries }),
  });
}

export function useWebhookDeliveries(query: WebhookDeliveryQuery, enabled = true) {
  return useQuery({
    queryKey: integrationKeys.deliveryList(query),
    queryFn: () => listWebhookDeliveries(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

/** One delivery with its envelope and attempt log; `pollMs` polls until the delivery is finished (test ping). */
export function useWebhookDelivery(id: string | undefined, pollMs?: number) {
  return useQuery({
    queryKey: integrationKeys.delivery(id ?? ""),
    queryFn: () => getWebhookDelivery(id as string),
    enabled: !!id,
    refetchInterval: pollMs
      ? (query) =>
          query.state.data && DELIVERY_FINAL_STATUSES.includes(query.state.data.status) ? false : pollMs
      : false,
  });
}

export function useRedeliverWebhookDelivery() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => redeliverWebhookDelivery(id),
    onSettled: () => queryClient.invalidateQueries({ queryKey: integrationKeys.deliveries }),
  });
}

export function useApiKeys(query: ApiKeyListQuery, enabled = true) {
  return useQuery({
    queryKey: integrationKeys.apiKeyList(query),
    queryFn: () => listApiKeys(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useCreateApiKey() {
  const queryClient = useQueryClient();
  return useMutation({
    ...SECRET_MUTATION,
    mutationFn: (input: ApiKeyInput) => createApiKey(input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: integrationKeys.apiKeys });
      void queryClient.invalidateQueries({ queryKey: integrationKeys.status });
    },
  });
}

export function useUpdateApiKey() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, patch }: { id: string; patch: ApiKeyPatch }) => updateApiKey(id, patch),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: integrationKeys.apiKeys }),
  });
}

export function useRevokeApiKey() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => revokeApiKey(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: integrationKeys.apiKeys });
      void queryClient.invalidateQueries({ queryKey: integrationKeys.status });
    },
  });
}

export function useDeleteApiKey() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => deleteApiKey(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: integrationKeys.apiKeys });
      void queryClient.invalidateQueries({ queryKey: integrationKeys.status });
    },
  });
}

export function useApiKeyUsage(id: string | undefined, from: string, to: string) {
  return useQuery({
    queryKey: integrationKeys.apiKeyUsage(id ?? "", from, to),
    queryFn: () => getApiKeyUsage(id as string, from, to),
    enabled: !!id,
  });
}
