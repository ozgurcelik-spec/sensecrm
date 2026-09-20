import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Modal, Radio, Stack, Text, Textarea } from "@mantine/core";
import { StepUpField } from "@/components/platform/step-up-field";
import { useSuspendPlatformOrganization } from "@/hooks/use-platform";
import { toast, toastApiError } from "@/hooks/use-toast";
import { SUSPEND_REASON_MAX, serverFieldErrors, stepUpFieldError } from "@/lib/platform";
import type { PlatformSuspensionMode } from "@/types";

interface SuspendDialogProps {
  tenantId: string;
  organizationName: string;
  onClose: () => void;
}

/** Suspend an organization: a reason is required; the mode decides between read-only and a full block. */
export function SuspendDialog({ tenantId, organizationName, onClose }: SuspendDialogProps) {
  const { t } = useTranslation(["platform", "common"]);
  const suspend = useSuspendPlatformOrganization(tenantId);
  const [reason, setReason] = useState("");
  const [mode, setMode] = useState<PlatformSuspensionMode>("readOnly");
  const [reasonError, setReasonError] = useState<string | null>(null);
  const [password, setPassword] = useState("");
  const [passwordError, setPasswordError] = useState<string | null>(null);
  /** The server asks for the caller's own password only for a full block. */
  const needsPassword = mode === "blocked";

  async function submit() {
    const trimmed = reason.trim();
    if (!trimmed) {
      setReasonError(t("platform:suspend.reasonRequired"));
      return;
    }
    setReasonError(null);
    setPasswordError(null);
    try {
      await suspend.mutateAsync({
        reason: trimmed,
        mode,
        ...(needsPassword ? { currentPassword: password } : {}),
      });
      toast({ variant: "success", description: t("platform:suspend.done", { name: organizationName }) });
      onClose();
    } catch (error) {
      const stepUp = stepUpFieldError(error);
      if (stepUp?.field === "password") {
        setPasswordError(stepUp.message);
        return;
      }
      const fields = serverFieldErrors(error);
      if (fields.reason) setReasonError(fields.reason);
      else toastApiError(error);
    }
  }

  return (
    <Modal opened onClose={onClose} title={t("platform:suspend.title")} centered>
      <Stack gap="md">
        <Text size="sm">{t("platform:suspend.intro", { name: organizationName })}</Text>
        <Textarea
          label={t("platform:suspend.reason")}
          withAsterisk
          autosize
          minRows={2}
          maxLength={SUSPEND_REASON_MAX}
          value={reason}
          onChange={(event) => {
            setReason(event.currentTarget.value);
            setReasonError(null);
          }}
          description={t("platform:reasonHint")}
          error={reasonError}
          data-autofocus
        />
        <Radio.Group
          label={t("platform:suspend.mode")}
          value={mode}
          onChange={(value) => setMode(value as PlatformSuspensionMode)}
        >
          <Stack gap="xs" mt="xs">
            <Radio
              value="readOnly"
              label={t("platform:suspend.readOnly")}
              description={t("platform:suspend.readOnlyHint")}
            />
            <Radio
              value="blocked"
              label={t("platform:suspend.blocked")}
              description={t("platform:suspend.blockedHint")}
            />
          </Stack>
        </Radio.Group>
        {needsPassword && (
          <StepUpField
            value={password}
            onChange={(value) => {
              setPassword(value);
              setPasswordError(null);
            }}
            error={passwordError}
          />
        )}
        <Group justify="flex-end" mt="sm">
          <Button variant="default" onClick={onClose} disabled={suspend.isPending}>
            {t("common:cancel")}
          </Button>
          <Button
            color="red"
            onClick={() => void submit()}
            loading={suspend.isPending}
            disabled={needsPassword && password === ""}
          >
            {t("platform:suspend.confirm")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
