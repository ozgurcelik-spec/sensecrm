import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Stack } from "@mantine/core";
import { Send } from "lucide-react";
import { useDelivery, useSendTestEmail } from "@/hooks/use-notifications";
import { toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import {
  deliveryReasonText,
  NOTIFICATION_EMAIL_NOT_CONFIGURED,
  RATE_LIMIT_EXCEEDED,
  retryAfterSeconds,
} from "@/lib/notifications";

/** How often the result of the test mail is looked up, and how long the screen waits for it. */
export const TEST_EMAIL_POLL_MS = 2_000;
export const TEST_EMAIL_TIMEOUT_MS = 60_000;

type Problem = "notConfigured" | "rateLimited";

interface TestEmailButtonProps {
  /** `false`: the server is not configured for e-mail at all, the button explains instead of trying. */
  availableByPlatform: boolean;
}

/**
 * "Send test e-mail": the server queues one mail to the caller's own address (202 + delivery id) and
 * the result is looked up every 2 s until it is sent, dead or skipped. A 429 disables the button
 * with a countdown (`Retry-After`, else 60 s), `409 notification.email_not_configured` explains.
 */
export function TestEmailButton({ availableByPlatform }: TestEmailButtonProps) {
  const { t } = useTranslation(["notifications"]);
  const send = useSendTestEmail();
  const [deliveryId, setDeliveryId] = useState<string | undefined>();
  const [startedAt, setStartedAt] = useState<number | undefined>();
  const [blockedUntil, setBlockedUntil] = useState<number | undefined>();
  const [problem, setProblem] = useState<Problem | undefined>();
  const [now, setNow] = useState(() => Date.now());

  const timedOut = startedAt !== undefined && now - startedAt > TEST_EMAIL_TIMEOUT_MS;
  const delivery = useDelivery(deliveryId, timedOut ? undefined : TEST_EMAIL_POLL_MS);
  const status = delivery.data?.status;
  const finished = status === "sent" || status === "dead" || status === "skipped";
  const waiting = !!deliveryId && !finished && !timedOut;
  const remaining = blockedUntil ? Math.max(0, Math.ceil((blockedUntil - now) / 1000)) : 0;
  const ticking = waiting || remaining > 0;

  useEffect(() => {
    if (!ticking) return;
    const id = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, [ticking]);

  async function onSend() {
    setProblem(undefined);
    setDeliveryId(undefined);
    setStartedAt(undefined);
    try {
      const result = await send.mutateAsync();
      const started = Date.now();
      setNow(started);
      setStartedAt(started);
      setDeliveryId(result.deliveryId);
    } catch (error) {
      const code = getApiProblem(error)?.code;
      if (code === RATE_LIMIT_EXCEEDED) {
        const started = Date.now();
        setNow(started);
        setBlockedUntil(started + retryAfterSeconds(error) * 1000);
        setProblem("rateLimited");
      } else if (code === NOTIFICATION_EMAIL_NOT_CONFIGURED) {
        setProblem("notConfigured");
      } else {
        toastApiError(error);
      }
    }
  }

  const disabled = !availableByPlatform || remaining > 0 || waiting;
  return (
    <Stack gap="xs" align="flex-start">
      <Button
        variant="default"
        leftSection={<Send size={16} />}
        disabled={disabled}
        loading={send.isPending}
        onClick={() => void onSend()}
      >
        {remaining > 0
          ? t("notifications:test.buttonWait", { seconds: remaining })
          : t("notifications:test.button")}
      </Button>
      <Stack gap="xs" w="100%" aria-live="polite" data-testid="test-email-result">
        {!availableByPlatform && (
          <Alert color="yellow" variant="light">
            {t("notifications:test.notConfigured")}
          </Alert>
        )}
        {problem === "notConfigured" && (
          <Alert color="yellow" variant="light" role="alert">
            {t("notifications:test.notConfigured")}
          </Alert>
        )}
        {problem === "rateLimited" && remaining > 0 && (
          <Alert color="orange" variant="light" role="alert">
            {t("notifications:test.rateLimited", { seconds: remaining })}
          </Alert>
        )}
        {waiting && (
          <Alert color="blue" variant="light">
            {t("notifications:test.sending")}
          </Alert>
        )}
        {timedOut && !finished && (
          <Alert color="blue" variant="light">
            {t("notifications:test.stillQueued")}
          </Alert>
        )}
        {status === "sent" && (
          <Alert color="green" variant="light">
            {t("notifications:test.sent")}
          </Alert>
        )}
        {status === "dead" && delivery.data && (
          <Alert color="red" variant="light" role="alert">
            {t("notifications:test.failed", { reason: deliveryReasonText(delivery.data) ?? "-" })}
          </Alert>
        )}
        {status === "skipped" && delivery.data && (
          <Alert color="yellow" variant="light" role="alert">
            {t("notifications:test.skipped", { reason: deliveryReasonText(delivery.data) ?? "-" })}
          </Alert>
        )}
      </Stack>
    </Stack>
  );
}
