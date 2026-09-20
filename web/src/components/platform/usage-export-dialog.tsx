import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Modal, SimpleGrid, Stack, Text, TextInput } from "@mantine/core";
import { Download } from "lucide-react";
import { useExportPlatformUsage } from "@/hooks/use-platform";
import { toast, toastApiError } from "@/hooks/use-toast";
import { MAX_USAGE_RANGE_DAYS, dayDiff, downloadBlob, isDay } from "@/lib/platform";

/** `error` key (under `platform:export.errors`) for an invalid range, or null when it can be sent. */
function rangeError(from: string, to: string): "incomplete" | "reversed" | "tooLong" | null {
  if (!from && !to) return null;
  if (!isDay(from) || !isDay(to)) return "incomplete";
  const days = dayDiff(from, to);
  if (days < 0) return "reversed";
  if (days + 1 > MAX_USAGE_RANGE_DAYS) return "tooLong";
  return null;
}

/**
 * Finance CSV of the daily usage snapshots (`GET /platform/usage/export`). Both dates empty = the
 * server's default, the previous calendar month; otherwise an inclusive UTC day range of at most 400 days.
 */
export function UsageExportDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation(["platform", "common"]);
  const exportUsage = useExportPlatformUsage();
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const error = rangeError(from, to);

  async function submit() {
    try {
      const blob = await exportUsage.mutateAsync({ from: from || undefined, to: to || undefined });
      downloadBlob(from && to ? `usage-${from}-${to}.csv` : "usage-previous-month.csv", blob);
      toast({ variant: "success", description: t("platform:export.done") });
      onClose();
    } catch (err) {
      toastApiError(err);
    }
  }

  return (
    <Modal opened onClose={onClose} title={t("platform:export.title")} centered>
      <Stack gap="md">
        <Text size="sm">{t("platform:export.intro")}</Text>
        <SimpleGrid cols={2} spacing="md">
          <TextInput
            type="date"
            label={t("platform:export.from")}
            value={from}
            max={to || undefined}
            onChange={(event) => setFrom(event.currentTarget.value)}
          />
          <TextInput
            type="date"
            label={t("platform:export.to")}
            value={to}
            min={from || undefined}
            onChange={(event) => setTo(event.currentTarget.value)}
          />
        </SimpleGrid>
        <Text size="xs" c={error ? "red" : "dimmed"} role={error ? "alert" : undefined}>
          {error ? t(`platform:export.errors.${error}`) : t("platform:export.hint")}
        </Text>
        <Group justify="flex-end" mt="sm">
          <Button variant="default" onClick={onClose} disabled={exportUsage.isPending}>
            {t("common:cancel")}
          </Button>
          <Button
            leftSection={<Download size={16} />}
            onClick={() => void submit()}
            loading={exportUsage.isPending}
            disabled={error !== null}
          >
            {t("platform:export.download")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
