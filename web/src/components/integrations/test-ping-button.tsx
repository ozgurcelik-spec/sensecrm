import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActionIcon, Tooltip } from "@mantine/core";
import { Send } from "lucide-react";
import { useTestWebhook, useWebhookDelivery } from "@/hooks/use-integrations";
import { toast, toastApiError } from "@/hooks/use-toast";
import { DELIVERY_FINAL_STATUSES, PING_POLL_MS, PING_TIMEOUT_MS, failureReasonText } from "@/lib/integrations";
import type { WebhookSubscription } from "@/types";

interface TestPingButtonProps {
  webhook: WebhookSubscription;
  /** `false`: this installation sends no webhooks, the button explains instead of trying. */
  webhooksEnabled: boolean;
  /** Called with the ping's delivery id once its result is known (opens the detail drawer). */
  onResult?: (deliveryId: string) => void;
}

/**
 * "Send test ping": the server queues a `ping` delivery (202 + delivery id), the worker sends it
 * within seconds. The delivery is polled every 2 s for up to 15 s and the outcome is toasted;
 * the parent can open the delivery drawer with it. Disabled with a hint while webhooks are off.
 */
export function TestPingButton({ webhook, webhooksEnabled, onResult }: TestPingButtonProps) {
  const { t } = useTranslation(["integrations"]);
  const send = useTestWebhook();
  const [deliveryId, setDeliveryId] = useState<string | undefined>();
  const [timedOut, setTimedOut] = useState(false);
  const delivery = useWebhookDelivery(timedOut ? undefined : deliveryId, PING_POLL_MS);
  const status = delivery.data?.status;
  const finished = !!status && DELIVERY_FINAL_STATUSES.includes(status);
  const waiting = !!deliveryId && !finished && !timedOut;

  useEffect(() => {
    if (!deliveryId || finished) return;
    const timer = window.setTimeout(() => setTimedOut(true), PING_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [deliveryId, finished]);

  // The outcome is announced once per ping.
  const announced = useRef<string | undefined>(undefined);
  useEffect(() => {
    if (!deliveryId || announced.current === deliveryId) return;
    if (finished && delivery.data) {
      announced.current = deliveryId;
      if (delivery.data.status === "succeeded") {
        toast({
          variant: "success",
          description: t("integrations:test.succeeded", { name: webhook.name, status: delivery.data.responseStatus ?? "-" }),
        });
      } else {
        toast({
          variant: "destructive",
          title: t("integrations:test.failedTitle"),
          description: t("integrations:test.failed", {
            name: webhook.name,
            reason: failureReasonText(delivery.data.failureReason) ?? "-",
          }),
        });
      }
      onResult?.(deliveryId);
    } else if (timedOut) {
      announced.current = deliveryId;
      toast({ description: t("integrations:test.stillQueued", { name: webhook.name }) });
      onResult?.(deliveryId);
    }
  }, [finished, timedOut, deliveryId, delivery.data, webhook.name, onResult, t]);

  async function onSend() {
    setTimedOut(false);
    try {
      const result = await send.mutateAsync(webhook.id);
      setDeliveryId(result.deliveryId);
    } catch (error) {
      toastApiError(error);
    }
  }

  const label = t("integrations:test.button", { name: webhook.name });
  return (
    <Tooltip label={webhooksEnabled ? t("integrations:test.tooltip") : t("integrations:test.unavailable")} multiline maw={260}>
      <ActionIcon
        variant="subtle"
        aria-label={label}
        disabled={!webhooksEnabled || waiting}
        loading={send.isPending || waiting}
        onClick={() => void onSend()}
      >
        <Send size={16} />
      </ActionIcon>
    </Tooltip>
  );
}
