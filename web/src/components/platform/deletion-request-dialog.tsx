import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Group, Modal, NumberInput, Stack, Text, TextInput, Textarea } from "@mantine/core";
import { TriangleAlert } from "lucide-react";
import { StepUpField } from "@/components/platform/step-up-field";
import { useRequestPlatformDeletion } from "@/hooks/use-platform";
import { toast, toastApiError } from "@/hooks/use-toast";
import {
  DEFAULT_RETENTION_DAYS,
  MAX_RETENTION_DAYS,
  MIN_RETENTION_DAYS,
  SUSPEND_REASON_MAX,
  serverFieldErrors,
  stepUpFieldError,
} from "@/lib/platform";
import { formatDateTime } from "@/lib/dates";

interface DeletionRequestDialogProps {
  tenantId: string;
  organizationName: string;
  onClose: () => void;
}

/**
 * KVKK deletion request: reason, retention window (7-90 days) and a "permanent destruction" warning.
 * The button only enables once the organization's name is typed exactly.
 */
export function DeletionRequestDialog({
  tenantId,
  organizationName,
  onClose,
}: DeletionRequestDialogProps) {
  const { t } = useTranslation(["platform", "common"]);
  const request = useRequestPlatformDeletion(tenantId);
  const [reason, setReason] = useState("");
  const [retention, setRetention] = useState<number | "">(DEFAULT_RETENTION_DAYS);
  const [confirmName, setConfirmName] = useState("");
  const [password, setPassword] = useState("");
  const [errors, setErrors] = useState<{
    reason?: string;
    retentionDays?: string;
    confirmTenantName?: string;
    password?: string;
  }>({});

  const retentionValid =
    typeof retention === "number" &&
    Number.isInteger(retention) &&
    retention >= MIN_RETENTION_DAYS &&
    retention <= MAX_RETENTION_DAYS;
  const canSubmit =
    reason.trim() !== "" && retentionValid && confirmName === organizationName && password !== "";

  async function submit() {
    setErrors({});
    try {
      const result = await request.mutateAsync({
        reason: reason.trim(),
        retentionDays: retention === "" ? undefined : retention,
        confirmTenantName: confirmName,
        currentPassword: password,
      });
      toast({
        variant: "success",
        description: t("platform:deletion.done", { date: formatDateTime(result.scheduledFor) }),
      });
      onClose();
    } catch (error) {
      const stepUp = stepUpFieldError(error);
      if (stepUp) {
        setErrors({ [stepUp.field]: stepUp.message });
        return;
      }
      const fields = serverFieldErrors(error);
      if (fields.reason || fields.retentionDays) {
        setErrors({ reason: fields.reason, retentionDays: fields.retentionDays });
      } else {
        toastApiError(error);
      }
    }
  }

  return (
    <Modal opened onClose={onClose} title={t("platform:deletion.title")} centered>
      <Stack gap="md">
        <Alert color="red" variant="light" icon={<TriangleAlert size={16} />}>
          {t("platform:deletion.warning", { days: retention === "" ? "-" : retention })}
        </Alert>
        <Textarea
          label={t("platform:deletion.reason")}
          withAsterisk
          autosize
          minRows={2}
          maxLength={SUSPEND_REASON_MAX}
          value={reason}
          onChange={(event) => setReason(event.currentTarget.value)}
          description={t("platform:reasonHint")}
          error={errors.reason}
          data-autofocus
        />
        <NumberInput
          label={t("platform:deletion.retentionDays")}
          description={t("platform:deletion.retentionHint", {
            min: MIN_RETENTION_DAYS,
            max: MAX_RETENTION_DAYS,
          })}
          min={MIN_RETENTION_DAYS}
          max={MAX_RETENTION_DAYS}
          allowDecimal={false}
          allowNegative={false}
          clampBehavior="none"
          value={retention}
          onChange={(value) => setRetention(typeof value === "number" ? value : "")}
          error={
            !retentionValid
              ? t("platform:deletion.retentionInvalid", {
                  min: MIN_RETENTION_DAYS,
                  max: MAX_RETENTION_DAYS,
                })
              : errors.retentionDays
          }
        />
        <TextInput
          label={t("platform:deletion.confirmLabel", { name: organizationName })}
          value={confirmName}
          onChange={(event) => {
            setConfirmName(event.currentTarget.value);
            setErrors((current) => ({ ...current, confirmTenantName: undefined }));
          }}
          error={errors.confirmTenantName}
          autoComplete="off"
        />
        <Text size="xs" c="dimmed">
          {t("platform:deletion.confirmHint")}
        </Text>
        <StepUpField
          value={password}
          onChange={(value) => {
            setPassword(value);
            setErrors((current) => ({ ...current, password: undefined }));
          }}
          error={errors.password}
        />
        <Group justify="flex-end" mt="sm">
          <Button variant="default" onClick={onClose} disabled={request.isPending}>
            {t("common:cancel")}
          </Button>
          <Button
            color="red"
            onClick={() => void submit()}
            loading={request.isPending}
            disabled={!canSubmit}
          >
            {t("platform:deletion.confirm")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
