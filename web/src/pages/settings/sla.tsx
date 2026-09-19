import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Card, Group, NumberInput, Skeleton, Stack, Table, Text } from "@mantine/core";
import { LoadError } from "@/components/load-error";
import { PageHeader } from "@/components/page-header";
import { useSlaPolicies, useUpdateSlaPolicies } from "@/hooks/use-sla-policies";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import { formatDuration } from "@/lib/case";
import { CASE_PRIORITIES, type CasePriority, type SlaPolicy } from "@/types";

/** Server limit: one year in minutes. */
const MAX_SLA_MINUTES = 525_600;

type Minutes = number | "";

interface Draft {
  priority: CasePriority;
  firstResponseMinutes: Minutes;
  resolutionMinutes: Minutes;
}

type Field = "firstResponseMinutes" | "resolutionMinutes";
type RowErrors = Partial<Record<Field, string>>;

const toDrafts = (policies: readonly SlaPolicy[]): Draft[] =>
  CASE_PRIORITIES.map((priority) => {
    const policy = policies.find((p) => p.priority === priority);
    return {
      priority,
      firstResponseMinutes: policy?.firstResponseMinutes ?? "",
      resolutionMinutes: policy?.resolutionMinutes ?? "",
    };
  });

const snapshot = (drafts: readonly Draft[]) =>
  JSON.stringify(drafts.map((d) => [d.priority, d.firstResponseMinutes, d.resolutionMinutes]));

const isValidMinutes = (value: Minutes): value is number =>
  typeof value === "number" && Number.isInteger(value) && value >= 1 && value <= MAX_SLA_MINUTES;

/** Client mirror of the server rule: 1 <= first response <= resolution <= 525600, whole minutes. */
function validateRow(draft: Draft): RowErrors {
  const errors: RowErrors = {};
  if (!isValidMinutes(draft.firstResponseMinutes)) errors.firstResponseMinutes = "range";
  if (!isValidMinutes(draft.resolutionMinutes)) errors.resolutionMinutes = "range";
  if (
    !errors.firstResponseMinutes &&
    !errors.resolutionMinutes &&
    (draft.firstResponseMinutes as number) > (draft.resolutionMinutes as number)
  ) {
    errors.firstResponseMinutes = "order";
  }
  return errors;
}

const SERVER_PATH = /^policies\[(\d+)\]\.(firstResponseMinutes|resolutionMinutes)$/i;

function PolicyEditor({ policies }: { policies: SlaPolicy[] }) {
  const { t } = useTranslation(["service", "common"]);
  const save = useUpdateSlaPolicies();
  const [drafts, setDrafts] = useState<Draft[]>(() => toDrafts(policies));
  const [submitted, setSubmitted] = useState(false);
  const [serverRows, setServerRows] = useState<Record<number, RowErrors>>({});
  const [serverGeneral, setServerGeneral] = useState<string | undefined>();

  const rowErrors = drafts.map(validateRow);
  const invalid = rowErrors.some((e) => Object.keys(e).length > 0);
  const dirty = snapshot(toDrafts(policies)) !== snapshot(drafts);

  function patch(index: number, change: Partial<Draft>) {
    setDrafts((list) => list.map((d, i) => (i === index ? { ...d, ...change } : d)));
    setServerRows((current) => {
      if (!current[index]) return current;
      const rest = { ...current };
      delete rest[index];
      return rest;
    });
  }

  async function onSave() {
    setSubmitted(true);
    setServerGeneral(undefined);
    if (invalid) return;
    const payload: SlaPolicy[] = drafts.map((d) => ({
      priority: d.priority,
      firstResponseMinutes: d.firstResponseMinutes as number,
      resolutionMinutes: d.resolutionMinutes as number,
    }));
    try {
      await save.mutateAsync(payload);
      toast({ variant: "success", description: t("service:sla.settings.saved") });
      setSubmitted(false);
      setServerRows({});
    } catch (error) {
      const errors = getApiProblem(error)?.errors;
      const rows: Record<number, RowErrors> = {};
      let general: string | undefined;
      let matched = false;
      for (const [path, messages] of Object.entries(errors ?? {})) {
        const message = messages[0];
        if (!message) continue;
        const hit = SERVER_PATH.exec(path);
        if (hit) {
          const index = Number(hit[1]);
          const field = (
            hit[2]?.toLowerCase() === "firstresponseminutes"
              ? "firstResponseMinutes"
              : "resolutionMinutes"
          ) as Field;
          rows[index] = { ...rows[index], [field]: message };
          matched = true;
        } else if (path.toLowerCase() === "policies") {
          general = message;
          matched = true;
        }
      }
      if (matched) {
        setServerRows(rows);
        setServerGeneral(general);
      } else {
        toastApiError(error);
      }
    }
  }

  const fieldError = (index: number, field: Field): string | undefined => {
    const server = serverRows[index]?.[field];
    if (server) return server;
    const local = rowErrors[index]?.[field];
    if (!submitted || !local) return undefined;
    return local === "order"
      ? t("service:sla.settings.orderError")
      : t("service:sla.settings.rangeError", { max: MAX_SLA_MINUTES });
  };

  const human = (value: Minutes) =>
    isValidMinutes(value) ? `= ${formatDuration(value)}` : "";

  return (
    <Stack gap="md">
      <Alert variant="light" color="blue">
        {t("service:sla.settings.note")}
      </Alert>
      {serverGeneral && (
        <Alert variant="light" color="red" role="alert">
          {serverGeneral}
        </Alert>
      )}
      <Card withBorder padding={0}>
        <Table.ScrollContainer minWidth={620}>
          <Table verticalSpacing="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("service:fields.priority")}</Table.Th>
                <Table.Th>{t("service:sla.settings.firstResponse")}</Table.Th>
                <Table.Th>{t("service:sla.settings.resolution")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {drafts.map((draft, index) => (
                <Table.Tr key={draft.priority} data-testid="sla-row">
                  <Table.Td>
                    <Text fw={500}>{t(`service:priorities.${draft.priority}`)}</Text>
                  </Table.Td>
                  {(["firstResponseMinutes", "resolutionMinutes"] as const).map((field) => (
                    <Table.Td key={field}>
                      <Group gap="sm" align="flex-start" wrap="nowrap">
                        <NumberInput
                          aria-label={`${t(`service:priorities.${draft.priority}`)} - ${t(
                            field === "firstResponseMinutes"
                              ? "service:sla.settings.firstResponse"
                              : "service:sla.settings.resolution"
                          )}`}
                          w={140}
                          min={1}
                          max={MAX_SLA_MINUTES}
                          allowDecimal={false}
                          allowNegative={false}
                          // Out-of-range input stays visible and is reported, not silently clamped on blur.
                          clampBehavior="none"
                          hideControls
                          value={draft[field]}
                          onChange={(value) =>
                            patch(index, {
                              [field]: typeof value === "number" ? value : "",
                            } as Partial<Draft>)
                          }
                          error={fieldError(index, field)}
                        />
                        <Text size="sm" c="dimmed" pt={6} style={{ whiteSpace: "nowrap" }}>
                          {human(draft[field])}
                        </Text>
                      </Group>
                    </Table.Td>
                  ))}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Card>
      <Group justify="flex-end">
        <Button onClick={() => void onSave()} disabled={!dirty} loading={save.isPending}>
          {t("common:save")}
        </Button>
      </Group>
    </Stack>
  );
}

/** Settings > SLA policies (`org.settings.manage`): first response and resolution minutes per priority. */
export default function SlaSettingsPage() {
  const { t } = useTranslation(["service"]);
  const { data, isLoading, error, refetch } = useSlaPolicies();

  return (
    <>
      <PageHeader
        title={t("service:sla.settings.title")}
        description={t("service:sla.settings.description")}
      />
      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading || !data ? (
        <Skeleton h={220} />
      ) : (
        <PolicyEditor policies={data} />
      )}
    </>
  );
}
