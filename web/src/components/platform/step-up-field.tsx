import { useTranslation } from "react-i18next";
import { PasswordInput } from "@mantine/core";

interface StepUpFieldProps {
  value: string;
  onChange: (value: string) => void;
  /** Inline server error (`platform.step_up_required` / `platform.step_up_failed`). */
  error?: string | null;
  autoFocus?: boolean;
}

/**
 * The step-up re-authentication field shared by every destructive platform command: the calling
 * platform admin's OWN password (never the target organization's or user's).
 */
export function StepUpField({ value, onChange, error, autoFocus }: StepUpFieldProps) {
  const { t } = useTranslation(["platform"]);
  return (
    <PasswordInput
      label={t("platform:stepUp.label")}
      description={t("platform:stepUp.description")}
      withAsterisk
      autoComplete="current-password"
      value={value}
      onChange={(event) => onChange(event.currentTarget.value)}
      error={error ?? undefined}
      data-autofocus={autoFocus ? true : undefined}
    />
  );
}
