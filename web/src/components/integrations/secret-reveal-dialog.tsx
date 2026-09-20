import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Checkbox, CopyButton, Group, Modal, Stack, Text, TextInput } from "@mantine/core";
import { Check, Copy, TriangleAlert } from "lucide-react";

interface SecretRevealDialogProps {
  title: string;
  intro: ReactNode;
  /** Label of the read-only field ("Gizli anahtar", "API anahtarı"). */
  label: string;
  /** The raw secret / key. It exists only in this dialog's props: it is never written to any storage. */
  value: string;
  /** Extra content under the field (a `curl` example, a grace period note). */
  extra?: ReactNode;
  onClose: () => void;
}

/**
 * Shows a raw secret (webhook signing secret, API key) exactly once: the server never returns it
 * again. The dialog can only be closed with the button, and the button stays disabled until the
 * "I saved it" box is ticked; Escape and an outside click do nothing. The caller drops the value
 * when `onClose` runs (it is state of the parent, nothing is stored in `localStorage` /
 * `sessionStorage` or the query cache).
 */
export function SecretRevealDialog({ title, intro, label, value, extra, onClose }: SecretRevealDialogProps) {
  const { t } = useTranslation(["integrations"]);
  const [saved, setSaved] = useState(false);
  return (
    <Modal
      opened
      onClose={() => undefined}
      title={title}
      size="lg"
      centered
      withCloseButton={false}
      closeOnClickOutside={false}
      closeOnEscape={false}
    >
      <Stack gap="md" data-testid="secret-reveal">
        <Text size="sm">{intro}</Text>
        <Alert color="yellow" variant="light" icon={<TriangleAlert size={16} />}>
          {t("integrations:reveal.warning")}
        </Alert>
        <Group gap="xs" align="flex-end" wrap="nowrap">
          <TextInput
            label={label}
            value={value}
            readOnly
            flex={1}
            data-testid="secret-value"
            styles={{ input: { fontFamily: "var(--mantine-font-family-monospace)" } }}
            onFocus={(event) => event.currentTarget.select()}
          />
          <CopyButton value={value} timeout={2000}>
            {({ copied, copy }) => (
              <Button
                variant={copied ? "light" : "default"}
                color={copied ? "green" : undefined}
                leftSection={copied ? <Check size={16} /> : <Copy size={16} />}
                onClick={copy}
              >
                {copied ? t("integrations:reveal.copied") : t("integrations:reveal.copy")}
              </Button>
            )}
          </CopyButton>
        </Group>
        {extra}
        <Checkbox
          label={t("integrations:reveal.saved")}
          checked={saved}
          onChange={(event) => setSaved(event.currentTarget.checked)}
        />
        <Group justify="flex-end">
          <Button disabled={!saved} onClick={onClose}>
            {t("integrations:reveal.done")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
