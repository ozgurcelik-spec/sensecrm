import { useTranslation } from "react-i18next";
import { Alert, Button, CopyButton, Group, Modal, Stack, Text, TextInput } from "@mantine/core";
import { Check, Copy, TriangleAlert } from "lucide-react";

interface TemporaryPasswordDialogProps {
  email: string;
  temporaryPassword: string;
  onClose: () => void;
}

/**
 * Shows a new account's temporary password exactly once (the server never returns it again).
 * It can only be closed with the explicit button, so it is not dismissed by accident.
 */
export function TemporaryPasswordDialog({
  email,
  temporaryPassword,
  onClose,
}: TemporaryPasswordDialogProps) {
  const { t } = useTranslation(["security", "common"]);
  return (
    <Modal
      opened
      onClose={onClose}
      title={t("security:temporaryPassword.title")}
      centered
      withCloseButton={false}
      closeOnClickOutside={false}
      closeOnEscape={false}
    >
      <Stack gap="md">
        <Text size="sm">{t("security:temporaryPassword.intro", { email })}</Text>
        <Alert color="yellow" variant="light" icon={<TriangleAlert size={16} />}>
          {t("security:temporaryPassword.warning")}
        </Alert>
        <Group gap="xs" align="flex-end" wrap="nowrap">
          <TextInput
            id="temporary-password"
            label={t("security:temporaryPassword.label")}
            value={temporaryPassword}
            readOnly
            flex={1}
            styles={{ input: { fontFamily: "var(--mantine-font-family-monospace)" } }}
            onFocus={(event) => event.currentTarget.select()}
          />
          <CopyButton value={temporaryPassword} timeout={2000}>
            {({ copied, copy }) => (
              <Button
                variant={copied ? "light" : "default"}
                color={copied ? "green" : undefined}
                leftSection={copied ? <Check size={16} /> : <Copy size={16} />}
                onClick={copy}
              >
                {copied
                  ? t("security:temporaryPassword.copied")
                  : t("security:temporaryPassword.copy")}
              </Button>
            )}
          </CopyButton>
        </Group>
        <Group justify="flex-end">
          <Button onClick={onClose}>{t("security:temporaryPassword.done")}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
