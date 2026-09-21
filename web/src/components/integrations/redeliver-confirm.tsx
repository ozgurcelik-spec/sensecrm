import { useTranslation } from "react-i18next";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useRedeliverWebhookDelivery } from "@/hooks/use-integrations";

interface RedeliverConfirmProps {
  /** Delivery to send again; undefined = closed. */
  deliveryId: string | undefined;
  onClose: () => void;
}

/**
 * Confirmation before a delivery is sent again: the same event (same id and payload) goes out as a
 * new `redelivery`, signed with the current secret, so the receiver has to de-duplicate by `id`.
 * The 409 answers (`delivery.not_redeliverable`, `webhook.disabled`, `webhook.delivery_unavailable`)
 * and the 429 are worded by `integrations:errors.*`.
 */
export function RedeliverConfirm({ deliveryId, onClose }: RedeliverConfirmProps) {
  const { t } = useTranslation(["integrations"]);
  const redeliver = useRedeliverWebhookDelivery();

  async function onConfirm() {
    if (!deliveryId) return;
    try {
      await redeliver.mutateAsync(deliveryId);
      toast({ variant: "success", description: t("integrations:deliveries.redelivered") });
      onClose();
    } catch (error) {
      toastApiError(error);
      onClose();
    }
  }

  return (
    <ConfirmDialog
      opened={!!deliveryId}
      title={t("integrations:deliveries.redeliverTitle")}
      message={t("integrations:deliveries.redeliverMessage")}
      confirmLabel={t("integrations:deliveries.redeliver")}
      loading={redeliver.isPending}
      onConfirm={() => void onConfirm()}
      onClose={onClose}
    />
  );
}
