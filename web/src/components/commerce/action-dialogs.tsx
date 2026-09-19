import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Textarea, TextInput } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";

interface ReasonDialogProps {
  title: string;
  label: string;
  confirmLabel: string;
  /** Server limit of the reason (reject 1000, cancel 1000). */
  maxLength?: number;
  loading?: boolean;
  onConfirm: (reason: string | undefined) => void;
  onClose: () => void;
}

/** Optional free-text reason of a rejection / cancellation. Mount only while open (it starts empty). */
export function ReasonDialog({
  title,
  label,
  confirmLabel,
  maxLength = 1000,
  loading,
  onConfirm,
  onClose,
}: ReasonDialogProps) {
  const { t } = useTranslation(["commerce"]);
  const [reason, setReason] = useState("");
  const tooLong = reason.trim().length > maxLength;
  return (
    <FormDialog
      opened
      onClose={onClose}
      title={title}
      size="md"
      loading={loading}
      submitLabel={confirmLabel}
      onSubmit={(event) => {
        event.preventDefault();
        if (!tooLong) onConfirm(reason.trim() || undefined);
      }}
    >
      <Textarea
        label={label}
        description={t("commerce:dialogs.reasonOptional")}
        data-autofocus
        autosize
        minRows={3}
        value={reason}
        onChange={(event) => setReason(event.currentTarget.value)}
        error={tooLong ? t("commerce:validation.reasonMax", { max: maxLength }) : undefined}
      />
    </FormDialog>
  );
}

interface ExtendDialogProps {
  /** Today in the organization's calendar (`YYYY-MM-DD`): the earliest allowed date. */
  minDate: string;
  initialDate?: string;
  loading?: boolean;
  /** Field error returned by the server (`errors.validUntil`). */
  serverError?: string;
  onConfirm: (validUntil: string) => void;
  onClose: () => void;
}

/** New `validUntil` of a sent / expired quote. */
export function ExtendDialog({
  minDate,
  initialDate,
  loading,
  serverError,
  onConfirm,
  onClose,
}: ExtendDialogProps) {
  const { t } = useTranslation(["commerce", "auth"]);
  const [date, setDate] = useState(initialDate && initialDate >= minDate ? initialDate : "");
  const [touched, setTouched] = useState(false);
  const error = touched
    ? !date
      ? t("auth:validation.required")
      : date < minDate
        ? t("commerce:validation.validUntilPast")
        : undefined
    : undefined;
  return (
    <FormDialog
      opened
      onClose={onClose}
      title={t("commerce:dialogs.extendTitle")}
      size="sm"
      loading={loading}
      submitLabel={t("commerce:actions.extend")}
      onSubmit={(event) => {
        event.preventDefault();
        setTouched(true);
        if (date && date >= minDate) onConfirm(date);
      }}
    >
      <TextInput
        type="date"
        label={t("commerce:fields.validUntil")}
        data-autofocus
        withAsterisk
        min={minDate}
        value={date}
        onChange={(event) => setDate(event.currentTarget.value)}
        error={error ?? serverError}
      />
    </FormDialog>
  );
}
