import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, NumberInput } from "@mantine/core";
import { Info } from "lucide-react";
import { FormDialog } from "@/components/crm/form-dialog";
import { toastApiError } from "@/hooks/use-toast";
import { useRotateWebhookSecret } from "@/hooks/use-integrations";
import { ROTATE_GRACE_DEFAULT, ROTATE_GRACE_MAX, ROTATE_GRACE_MIN } from "@/lib/integrations";
import type { WebhookRotatedSecret, WebhookSubscription } from "@/types";

interface RotateSecretDialogProps {
  webhook: WebhookSubscription;
  onClose: () => void;
  /** The answer carries the new raw secret, which the caller shows exactly once. */
  onRotated: (rotated: WebhookRotatedSecret) => void;
}

const isValidGrace = (value: number | string): value is number =>
  typeof value === "number" && Number.isInteger(value) && value >= ROTATE_GRACE_MIN && value <= ROTATE_GRACE_MAX;

/**
 * Rotates the signing secret: the new one is valid at once, the old one keeps working for the grace
 * period (0 - 168 hours, default 24; 0 cuts it off immediately). Receivers must be updated in that time.
 */
export function RotateSecretDialog({ webhook, onClose, onRotated }: RotateSecretDialogProps) {
  const { t } = useTranslation(["integrations", "common"]);
  const rotate = useRotateWebhookSecret();
  const [grace, setGrace] = useState<number | string>(ROTATE_GRACE_DEFAULT);
  const [submitted, setSubmitted] = useState(false);
  const invalid = submitted && !isValidGrace(grace);

  async function onSubmit() {
    setSubmitted(true);
    if (!isValidGrace(grace)) return;
    try {
      const result = await rotate.mutateAsync({ id: webhook.id, graceHours: grace });
      onRotated(result);
      onClose();
    } catch (error) {
      toastApiError(error);
    }
  }

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={t("integrations:rotate.title", { name: webhook.name })}
      onSubmit={(event) => {
        event.preventDefault();
        void onSubmit();
      }}
      loading={rotate.isPending}
      submitLabel={t("integrations:rotate.submit")}
      size="md"
    >
      <Alert color="blue" variant="light" icon={<Info size={16} />}>
        {t("integrations:rotate.explain")}
      </Alert>
      <NumberInput
        label={t("integrations:rotate.graceHours")}
        description={t("integrations:rotate.graceHint", { min: ROTATE_GRACE_MIN, max: ROTATE_GRACE_MAX })}
        allowDecimal={false}
        allowNegative={false}
        clampBehavior="none"
        withAsterisk
        data-autofocus
        value={grace}
        onChange={setGrace}
        error={invalid ? t("integrations:rotate.graceInvalid", { min: ROTATE_GRACE_MIN, max: ROTATE_GRACE_MAX }) : undefined}
      />
    </FormDialog>
  );
}
