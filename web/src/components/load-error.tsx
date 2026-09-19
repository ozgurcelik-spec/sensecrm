import { useTranslation } from "react-i18next";
import { Alert, Button, Group } from "@mantine/core";
import { getApiErrorMessage } from "@/lib/api-error";

/** Inline "could not load" alert with a retry button for query-backed screens. */
export function LoadError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const { t } = useTranslation(["common"]);
  return (
    <Alert color="red" variant="light" title={t("common:loadFailed")}>
      <Group justify="space-between" wrap="nowrap">
        {getApiErrorMessage(error)}
        <Button size="xs" variant="default" onClick={onRetry}>
          {t("common:retry")}
        </Button>
      </Group>
    </Alert>
  );
}
