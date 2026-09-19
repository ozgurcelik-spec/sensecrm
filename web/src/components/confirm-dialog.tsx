import { useTranslation } from "react-i18next";
import { Button, Group, Modal, Text } from "@mantine/core";

interface ConfirmDialogProps {
  opened: boolean;
  title: string;
  message: string;
  confirmLabel?: string;
  /** Red confirm button for destructive actions. */
  destructive?: boolean;
  loading?: boolean;
  onConfirm: () => void;
  onClose: () => void;
}

export function ConfirmDialog({
  opened,
  title,
  message,
  confirmLabel,
  destructive = false,
  loading = false,
  onConfirm,
  onClose,
}: ConfirmDialogProps) {
  const { t } = useTranslation(["common"]);
  return (
    <Modal opened={opened} onClose={onClose} title={title} centered>
      <Text size="sm">{message}</Text>
      <Group justify="flex-end" mt="lg">
        <Button variant="default" onClick={onClose} disabled={loading}>
          {t("common:cancel")}
        </Button>
        <Button color={destructive ? "red" : undefined} onClick={onConfirm} loading={loading}>
          {confirmLabel ?? t("common:confirm")}
        </Button>
      </Group>
    </Modal>
  );
}
