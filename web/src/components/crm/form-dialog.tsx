import type { FormEventHandler, ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Modal, ScrollArea, Stack } from "@mantine/core";

interface FormDialogProps {
  opened: boolean;
  onClose: () => void;
  title: string;
  onSubmit: FormEventHandler<HTMLFormElement>;
  loading?: boolean;
  submitLabel: string;
  size?: string;
  children: ReactNode;
}

/** Modal shell shared by the create/edit forms: title, scrollable body, cancel + submit footer. */
export function FormDialog({
  opened,
  onClose,
  title,
  onSubmit,
  loading = false,
  submitLabel,
  size = "lg",
  children,
}: FormDialogProps) {
  const { t } = useTranslation(["common"]);
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={title}
      size={size}
      centered
      scrollAreaComponent={ScrollArea.Autosize}
    >
      <form onSubmit={onSubmit} noValidate>
        <Stack gap="md">
          {children}
          <Group justify="flex-end" mt="sm">
            <Button variant="default" onClick={onClose} disabled={loading}>
              {t("common:cancel")}
            </Button>
            <Button type="submit" loading={loading}>
              {submitLabel}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
