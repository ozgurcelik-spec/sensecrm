import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Group, Modal, Stack, Text, TextInput } from "@mantine/core";
import { TriangleAlert } from "lucide-react";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useDeleteWebhook } from "@/hooks/use-integrations";
import type { WebhookSubscription } from "@/types";

/** Deleting a subscription removes its delivery log too: the name has to be typed to confirm. */
export function DeleteWebhookDialog({ webhook, onClose }: { webhook: WebhookSubscription; onClose: () => void }) {
  const { t } = useTranslation(["integrations", "common"]);
  const remove = useDeleteWebhook();
  const [typed, setTyped] = useState("");
  const matches = typed.trim() === webhook.name;

  async function onConfirm() {
    if (!matches) return;
    try {
      await remove.mutateAsync(webhook.id);
      toast({ variant: "success", description: t("integrations:webhooks.deleted", { name: webhook.name }) });
      onClose();
    } catch (error) {
      toastApiError(error);
    }
  }

  return (
    <Modal opened onClose={onClose} title={t("integrations:webhooks.deleteTitle", { name: webhook.name })} centered>
      <Stack gap="md">
        <Alert color="red" variant="light" icon={<TriangleAlert size={16} />}>
          {t("integrations:webhooks.deleteWarning")}
        </Alert>
        <Text size="sm">{t("integrations:webhooks.deleteType", { name: webhook.name })}</Text>
        <TextInput
          aria-label={t("integrations:webhooks.deleteTypeLabel")}
          value={typed}
          onChange={(event) => setTyped(event.currentTarget.value)}
          data-autofocus
          autoComplete="off"
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={remove.isPending}>
            {t("common:cancel")}
          </Button>
          <Button color="red" disabled={!matches} loading={remove.isPending} onClick={() => void onConfirm()}>
            {t("common:delete")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
