import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Group,
  Modal,
  NumberInput,
  Select,
  Skeleton,
  Stack,
  Switch,
  Text,
  TextInput,
  Tooltip,
} from "@mantine/core";
import { ArrowDown, ArrowUp, Plus, Trash2 } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { PageHeader } from "@/components/page-header";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import {
  useCreatePipeline,
  usePipelines,
  useUpdatePipeline,
  useUpdatePipelineStages,
} from "@/hooks/use-pipelines";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import { STAGE_KINDS, type Pipeline, type StageInput, type StageKind } from "@/types";

interface StageDraft {
  /** Stable React key (the server id for existing stages). */
  key: string;
  id?: string;
  name: string;
  probability: number;
  kind: StageKind;
}

let draftCounter = 0;
const newKey = () => `new-${(draftCounter += 1)}`;

const toDrafts = (pipeline: Pipeline): StageDraft[] =>
  [...pipeline.stages]
    .sort((a, b) => a.order - b.order)
    .map((s) => ({
      key: s.id,
      id: s.id,
      name: s.name,
      probability: s.probability,
      kind: s.kind,
    }));

const snapshot = (list: readonly StageDraft[]) =>
  JSON.stringify(
    list.map((s) => ({ id: s.id, name: s.name, probability: s.probability, kind: s.kind }))
  );

type StageProblem = "name" | "probability" | "won" | "lost";

/** Client-side mirror of the server rules: names and 0-100 probabilities, exactly one won and one lost stage. */
function validateStages(stages: readonly StageDraft[]): StageProblem[] {
  const problems: StageProblem[] = [];
  if (stages.some((s) => !s.name.trim())) problems.push("name");
  if (
    stages.some((s) => !Number.isFinite(s.probability) || s.probability < 0 || s.probability > 100)
  ) {
    problems.push("probability");
  }
  if (stages.filter((s) => s.kind === "won").length !== 1) problems.push("won");
  if (stages.filter((s) => s.kind === "lost").length !== 1) problems.push("lost");
  return problems;
}

function StageEditor({ pipeline, canManage }: { pipeline: Pipeline; canManage: boolean }) {
  const { t } = useTranslation(["crm", "common", "auth"]);
  const save = useUpdatePipelineStages();
  const [stages, setStages] = useState<StageDraft[]>(() => toDrafts(pipeline));
  const [submitted, setSubmitted] = useState(false);
  const problems = validateStages(stages);

  const patch = (key: string, change: Partial<StageDraft>) =>
    setStages((list) => list.map((s) => (s.key === key ? { ...s, ...change } : s)));

  function moveStage(index: number, delta: -1 | 1) {
    setStages((list) => {
      const target = index + delta;
      if (target < 0 || target >= list.length) return list;
      const next = [...list];
      const [item] = next.splice(index, 1);
      if (item) next.splice(target, 0, item);
      return next;
    });
  }

  async function onSave() {
    setSubmitted(true);
    if (problems.length > 0) return;
    const payload: StageInput[] = stages.map((s) => ({
      id: s.id,
      name: s.name.trim(),
      probability: s.probability,
      kind: s.kind,
    }));
    try {
      await save.mutateAsync({ id: pipeline.id, stages: payload });
      toast({ variant: "success", description: t("crm:pipelines.stagesSaved") });
      setSubmitted(false);
    } catch (error) {
      // pipeline.stage_in_use: a removed stage still holds deals.
      toastApiError(error);
    }
  }

  const dirty = snapshot(toDrafts(pipeline)) !== snapshot(stages);

  return (
    <Card withBorder padding="md">
      <Group justify="space-between" mb="sm">
        <Text fw={600}>{t("crm:pipelines.stages")}</Text>
        {canManage && (
          <Button
            size="xs"
            variant="light"
            leftSection={<Plus size={14} />}
            onClick={() =>
              setStages((list) => [
                ...list,
                { key: newKey(), name: "", probability: 0, kind: "open" },
              ])
            }
          >
            {t("crm:pipelines.addStage")}
          </Button>
        )}
      </Group>

      {!canManage && (
        <Alert variant="light" color="gray" mb="sm">
          {t("crm:pipelines.readOnly")}
        </Alert>
      )}

      <Stack gap="xs" component="ol" p={0} style={{ listStyle: "none", margin: 0 }}>
        {stages.map((stage, index) => (
          <Group
            key={stage.key}
            component="li"
            gap="xs"
            align="flex-start"
            wrap="wrap"
            data-testid="stage-row"
          >
            <Badge variant="light" color="gray" w={28} mt={6} aria-hidden="true">
              {index + 1}
            </Badge>
            <TextInput
              aria-label={t("crm:pipelines.stageName")}
              placeholder={t("crm:pipelines.stageName")}
              value={stage.name}
              disabled={!canManage}
              onChange={(event) => patch(stage.key, { name: event.currentTarget.value })}
              error={submitted && !stage.name.trim() ? t("auth:validation.required") : undefined}
              style={{ flex: "1 1 180px" }}
            />
            <NumberInput
              aria-label={t("crm:pipelines.probability")}
              suffix="%"
              min={0}
              max={100}
              clampBehavior="strict"
              hideControls
              w={90}
              value={stage.probability}
              disabled={!canManage}
              onChange={(value) =>
                patch(stage.key, { probability: typeof value === "number" ? value : 0 })
              }
            />
            <Select
              aria-label={t("crm:pipelines.kind")}
              w={140}
              allowDeselect={false}
              data={STAGE_KINDS.map((k) => ({ value: k, label: t(`crm:deals.kinds.${k}`) }))}
              value={stage.kind}
              disabled={!canManage}
              onChange={(value) => value && patch(stage.key, { kind: value as StageKind })}
            />
            {canManage && (
              <Group gap={2} wrap="nowrap" mt={2}>
                <Tooltip label={t("crm:pipelines.moveUp")}>
                  <ActionIcon
                    variant="subtle"
                    aria-label={t("crm:pipelines.moveUpNamed", { index: index + 1 })}
                    disabled={index === 0}
                    onClick={() => moveStage(index, -1)}
                  >
                    <ArrowUp size={16} />
                  </ActionIcon>
                </Tooltip>
                <Tooltip label={t("crm:pipelines.moveDown")}>
                  <ActionIcon
                    variant="subtle"
                    aria-label={t("crm:pipelines.moveDownNamed", { index: index + 1 })}
                    disabled={index === stages.length - 1}
                    onClick={() => moveStage(index, 1)}
                  >
                    <ArrowDown size={16} />
                  </ActionIcon>
                </Tooltip>
                <Tooltip label={t("crm:pipelines.removeStage")}>
                  <ActionIcon
                    variant="subtle"
                    color="red"
                    aria-label={t("crm:pipelines.removeStageNamed", { index: index + 1 })}
                    onClick={() => setStages((list) => list.filter((s) => s.key !== stage.key))}
                  >
                    <Trash2 size={16} />
                  </ActionIcon>
                </Tooltip>
              </Group>
            )}
          </Group>
        ))}
      </Stack>

      {submitted && problems.length > 0 && (
        <Alert color="red" variant="light" mt="sm" role="alert">
          {problems.map((p) => (
            <div key={p}>{t(`crm:pipelines.problems.${p}`)}</div>
          ))}
        </Alert>
      )}

      {canManage && (
        <Group justify="flex-end" mt="md">
          <Button
            variant="default"
            disabled={!dirty || save.isPending}
            onClick={() => {
              setStages(toDrafts(pipeline));
              setSubmitted(false);
            }}
          >
            {t("crm:pipelines.reset")}
          </Button>
          <Button onClick={() => void onSave()} loading={save.isPending} disabled={!dirty}>
            {t("crm:pipelines.saveStages")}
          </Button>
        </Group>
      )}
    </Card>
  );
}

function PipelineMeta({ pipeline, canManage }: { pipeline: Pipeline; canManage: boolean }) {
  const { t } = useTranslation(["crm", "common", "auth"]);
  const update = useUpdatePipeline();
  const [name, setName] = useState(pipeline.name);
  const [isDefault, setIsDefault] = useState(pipeline.isDefault);
  const dirty = name.trim() !== pipeline.name || isDefault !== pipeline.isDefault;

  async function onSave() {
    if (!name.trim()) return;
    try {
      await update.mutateAsync({ id: pipeline.id, name: name.trim(), isDefault });
      toast({ variant: "success", description: t("crm:pipelines.saved") });
    } catch (error) {
      // pipeline.default_required: the only default pipeline cannot be unset.
      toastApiError(error);
      setIsDefault(pipeline.isDefault);
    }
  }

  return (
    <Card withBorder padding="md">
      <Group align="flex-end" wrap="wrap">
        <TextInput
          label={t("crm:pipelines.name")}
          value={name}
          disabled={!canManage}
          onChange={(event) => setName(event.currentTarget.value)}
          error={!name.trim() ? t("auth:validation.required") : undefined}
          style={{ flex: "1 1 220px" }}
        />
        <Switch
          label={t("crm:pipelines.isDefault")}
          checked={isDefault}
          disabled={!canManage || pipeline.isDefault}
          onChange={(event) => setIsDefault(event.currentTarget.checked)}
          mb={8}
        />
        {canManage && (
          <Button
            onClick={() => void onSave()}
            loading={update.isPending}
            disabled={!dirty || !name.trim()}
          >
            {t("common:save")}
          </Button>
        )}
      </Group>
    </Card>
  );
}

function NewPipelineDialog({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (id: string) => void;
}) {
  const { t } = useTranslation(["crm", "common", "auth"]);
  const create = useCreatePipeline();
  const [name, setName] = useState("");
  const [touched, setTouched] = useState(false);
  const [serverError, setServerError] = useState<string | undefined>();

  return (
    <Modal opened onClose={onClose} title={t("crm:pipelines.createTitle")} centered>
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          setTouched(true);
          if (!name.trim()) return;
          create.mutate(name.trim(), {
            onSuccess: (pipeline) => {
              toast({ variant: "success", description: t("crm:pipelines.created") });
              onCreated(pipeline.id);
              onClose();
            },
            onError: (error) => {
              const message =
                getApiProblem(error)?.errors?.["name"]?.[0] ??
                getApiProblem(error)?.errors?.["Name"]?.[0];
              if (message) setServerError(message);
              else toastApiError(error);
            },
          });
        }}
      >
        <Stack gap="md">
          <TextInput
            label={t("crm:pipelines.name")}
            withAsterisk
            data-autofocus
            value={name}
            onChange={(event) => {
              setName(event.currentTarget.value);
              setServerError(undefined);
            }}
            error={
              serverError ?? (touched && !name.trim() ? t("auth:validation.required") : undefined)
            }
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose} disabled={create.isPending}>
              {t("common:cancel")}
            </Button>
            <Button type="submit" loading={create.isPending}>
              {t("common:create")}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}

export default function PipelinesPage() {
  const { t } = useTranslation(["crm", "common"]);
  const { canManagePipelines } = useCrmPermissions();
  const pipelines = usePipelines();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const selected = useMemo(
    () =>
      pipelines.data?.find((p) => p.id === selectedId) ??
      pipelines.data?.find((p) => p.isDefault) ??
      pipelines.data?.[0],
    [pipelines.data, selectedId]
  );

  // Re-mount the editors when the pipeline or its saved stages change so drafts follow the server.
  const editorKey = selected
    ? `${selected.id}:${selected.name}:${selected.isDefault}:${selected.stages.map((s) => `${s.id}/${s.name}/${s.probability}/${s.kind}/${s.order}`).join("|")}`
    : "none";

  return (
    <>
      <PageHeader
        title={t("crm:pipelines.title")}
        description={t("crm:pipelines.description")}
        actions={
          canManagePipelines && (
            <Button leftSection={<Plus size={16} />} onClick={() => setCreating(true)}>
              {t("crm:pipelines.new")}
            </Button>
          )
        }
      />
      <Stack gap="md" maw={900}>
        {pipelines.error && (
          <LoadError error={pipelines.error} onRetry={() => void pipelines.refetch()} />
        )}
        {pipelines.isLoading && <Skeleton h={200} />}
        {pipelines.data && pipelines.data.length > 1 && (
          <Select
            label={t("crm:pipelines.select")}
            allowDeselect={false}
            data={pipelines.data.map((p) => ({
              value: p.id,
              label: p.isDefault ? `${p.name} (${t("crm:pipelines.defaultTag")})` : p.name,
            }))}
            value={selected?.id ?? null}
            onChange={setSelectedId}
            w={320}
          />
        )}
        {selected && (
          <Stack gap="md" key={editorKey}>
            <PipelineMeta pipeline={selected} canManage={canManagePipelines} />
            <StageEditor pipeline={selected} canManage={canManagePipelines} />
          </Stack>
        )}
      </Stack>
      {creating && (
        <NewPipelineDialog onClose={() => setCreating(false)} onCreated={setSelectedId} />
      )}
    </>
  );
}
