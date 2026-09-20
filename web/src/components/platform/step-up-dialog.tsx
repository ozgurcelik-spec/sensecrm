import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Modal, Stack, Text } from "@mantine/core";
import { StepUpField } from "@/components/platform/step-up-field";
import { toastApiError } from "@/hooks/use-toast";
import { stepUpFieldError } from "@/lib/platform";

interface StepUpDialogProps {
  title: string;
  message?: string;
  /** Extra controls between the message and the password field (e.g. a checkbox). */
  children?: ReactNode;
  confirmLabel: string;
  isPending: boolean;
  /** Runs the command with the typed password; a rejected promise keeps the dialog open. */
  onConfirm: (currentPassword: string) => Promise<void>;
  onClose: () => void;
}

/** Small "type your own password" dialog for destructive platform commands that need nothing else. */
export function StepUpDialog({
  title,
  message,
  children,
  confirmLabel,
  isPending,
  onConfirm,
  onClose,
}: StepUpDialogProps) {
  const { t } = useTranslation(["common"]);
  const [password, setPassword] = useState("");
  const [passwordError, setPasswordError] = useState<string | null>(null);

  async function submit() {
    setPasswordError(null);
    try {
      await onConfirm(password);
    } catch (error) {
      const inline = stepUpFieldError(error);
      if (inline?.field === "password") setPasswordError(inline.message);
      else toastApiError(error);
    }
  }

  return (
    <Modal opened onClose={onClose} title={title} centered>
      <Stack gap="md">
        {message && <Text size="sm">{message}</Text>}
        {children}
        <StepUpField
          value={password}
          onChange={(value) => {
            setPassword(value);
            setPasswordError(null);
          }}
          error={passwordError}
          autoFocus
        />
        <Group justify="flex-end" mt="sm">
          <Button variant="default" onClick={onClose} disabled={isPending}>
            {t("common:cancel")}
          </Button>
          <Button
            color="red"
            onClick={() => void submit()}
            loading={isPending}
            disabled={password === ""}
          >
            {confirmLabel}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
