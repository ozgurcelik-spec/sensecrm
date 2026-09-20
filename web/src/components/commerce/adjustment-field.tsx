import { useTranslation } from "react-i18next";
import { Button, Group, NumberInput, Stack } from "@mantine/core";
import type { AdjustmentIssue } from "@/lib/commerce-totals";

interface AdjustmentFieldProps {
  value: number | string;
  onChange: (value: number | string) => void;
  /** "Yuvarla": sets the adjustment that rounds the grand total to whole currency units. */
  onRound: () => void;
  /** Client rule that is broken right now (an i18n key is derived from it) or a server text. */
  issue?: AdjustmentIssue;
  serverError?: string;
  disabled?: boolean;
  /** Nothing to round (no lines). */
  roundDisabled?: boolean;
}

/** Signed rounding line of the totals card: number input (negative allowed) plus the "Yuvarla" button. */
export function AdjustmentField({
  value,
  onChange,
  onRound,
  issue,
  serverError,
  disabled,
  roundDisabled,
}: AdjustmentFieldProps) {
  const { t } = useTranslation(["commerce"]);
  const error = serverError ?? (issue ? t(`commerce:validation.adjustment.${issue}`) : undefined);
  return (
    <Stack gap={4} align="flex-end">
      <Group gap="xs" wrap="nowrap">
        <NumberInput
          aria-label={t("commerce:totals.adjustment")}
          size="xs"
          w={130}
          hideControls
          allowNegative
          decimalScale={2}
          value={value}
          onChange={onChange}
          disabled={disabled}
          error={!!error}
          styles={{ input: { textAlign: "right" } }}
        />
        <Button
          variant="light"
          size="compact-sm"
          onClick={onRound}
          disabled={disabled || roundDisabled}
          title={t("commerce:totals.roundHint")}
        >
          {t("commerce:totals.round")}
        </Button>
      </Group>
      {error && (
        <span role="alert" style={{ color: "var(--mantine-color-red-6)", fontSize: 12 }}>
          {error}
        </span>
      )}
    </Stack>
  );
}
