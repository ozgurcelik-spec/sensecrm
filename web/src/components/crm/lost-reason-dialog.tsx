import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Modal, Stack, Text, Textarea } from "@mantine/core";

interface LostReasonDialogProps {
  dealName: string;
  loading: boolean;
  onConfirm: (reason: string) => void;
  onClose: () => void;
}

/** Asks for the mandatory lost reason before a deal is moved to a "lost" stage. */
export function LostReasonDialog({ dealName, loading, onConfirm, onClose }: LostReasonDialogProps) {
  const { t } = useTranslation(["crm", "common", "auth"]);
  const [reason, setReason] = useState("");
  const [touched, setTouched] = useState(false);
  const missing = touched && !reason.trim();

  return (
    <Modal opened onClose={onClose} title={t("crm:deals.lost.title")} centered>
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          setTouched(true);
          if (reason.trim()) onConfirm(reason.trim());
        }}
      >
        <Stack gap="md">
          <Text size="sm">{t("crm:deals.lost.message", { name: dealName })}</Text>
          <Textarea
            label={t("crm:deals.fields.lostReason")}
            withAsterisk
            data-autofocus
            autosize
            minRows={2}
            value={reason}
            onChange={(event) => setReason(event.currentTarget.value)}
            error={missing ? t("auth:validation.required") : undefined}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose} disabled={loading}>
              {t("common:cancel")}
            </Button>
            <Button type="submit" color="red" loading={loading}>
              {t("crm:deals.lost.confirm")}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
