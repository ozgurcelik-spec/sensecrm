import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Menu, Modal, Select, Stack, Text, Textarea } from "@mantine/core";
import { ChevronDown, Pencil, Trash2 } from "lucide-react";
import {
  useAssignCase,
  useChangeCasePriority,
  useChangeCaseStatus,
} from "@/hooks/use-cases";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiErrorStatus, getApiProblem } from "@/lib/api-error";
import {
  allowedTransitions,
  canReopenClosed,
  isActiveStatus,
  isReopen,
  needsResolutionNote,
} from "@/lib/case";
import { CASE_PRIORITIES, type CaseDetail, type CasePriority, type CaseStatus } from "@/types";
import { AssigneeSelect } from "./assignee-select";

const MAX_NOTE = 4000;

/** Menu / button label of moving a case from `from` to `to`. */
function transitionKey(from: CaseStatus, to: CaseStatus): string {
  if (isReopen(from, to)) return "reopen";
  if (to === "closed") return needsResolutionNote(from, to) ? "closeUnresolved" : "close";
  return to;
}

interface ResolutionDialogProps {
  from: CaseStatus;
  to: CaseStatus;
  loading: boolean;
  error?: string;
  onSubmit: (note: string) => void;
  onClose: () => void;
}

/** Asks for the resolution note that resolving (or closing an unresolved case) requires. */
function ResolutionDialog({ from, to, loading, error, onSubmit, onClose }: ResolutionDialogProps) {
  const { t } = useTranslation(["service", "common"]);
  const [note, setNote] = useState("");
  const [touched, setTouched] = useState(false);
  const empty = note.trim().length === 0;

  function submit() {
    setTouched(true);
    if (empty) return;
    onSubmit(note.trim());
  }

  return (
    <Modal
      opened
      onClose={onClose}
      centered
      title={t(`service:resolution.title.${transitionKey(from, to)}`)}
    >
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          {t("service:resolution.description")}
        </Text>
        <Textarea
          label={t("service:resolution.note")}
          withAsterisk
          data-autofocus
          autosize
          minRows={3}
          maxRows={8}
          maxLength={MAX_NOTE}
          value={note}
          onChange={(event) => setNote(event.currentTarget.value)}
          error={
            error ?? (touched && empty ? t("service:resolution.required") : undefined)
          }
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={loading}>
            {t("common:cancel")}
          </Button>
          <Button onClick={submit} loading={loading}>
            {t(`service:resolution.submit.${transitionKey(from, to)}`)}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

interface CaseActionsProps {
  item: CaseDetail;
  onEdit: () => void;
  onDelete: () => void;
  /** The case changed under us (409): the page reloads it and its timeline. */
  onStale: () => void;
}

/**
 * Write actions of the case detail (`crm.cases.write`; nothing at all without it): a status menu that
 * only offers the transitions of the state machine, priority and assignee selects, edit and delete.
 * Edit, priority and assignee are disabled once the case is no longer new / open / pending.
 */
export function CaseActions({ item, onEdit, onDelete, onStale }: CaseActionsProps) {
  const { t } = useTranslation(["service", "common"]);
  const { canWriteCases } = useCrmPermissions();
  const changeStatus = useChangeCaseStatus();
  const changePriority = useChangeCasePriority();
  const assign = useAssignCase();
  const [pending, setPending] = useState<CaseStatus | null>(null);
  const [noteError, setNoteError] = useState<string | undefined>();

  if (!canWriteCases) return null;

  const active = isActiveStatus(item.status);
  const targets = allowedTransitions(item.status).filter(
    (to) => !(item.status === "closed" && to === "open" && !canReopenClosed(item.closedAt))
  );

  /** Any 409 (conflict, invalid transition, closed window, not active) means our copy is stale. */
  function fail(error: unknown) {
    toastApiError(error);
    if (getApiErrorStatus(error) === 409) onStale();
  }

  async function move(to: CaseStatus, resolutionNote?: string) {
    setNoteError(undefined);
    try {
      await changeStatus.mutateAsync({ id: item.id, status: to, resolutionNote });
      toast({ variant: "success", description: t("service:statusUpdated") });
      setPending(null);
    } catch (error) {
      if (getApiProblem(error)?.code === "case.resolution_required") {
        setNoteError(t("common:errors.case.resolution_required"));
        return;
      }
      setPending(null);
      fail(error);
    }
  }

  function choose(to: CaseStatus) {
    if (needsResolutionNote(item.status, to)) {
      setNoteError(undefined);
      setPending(to);
    } else {
      void move(to);
    }
  }

  async function onPriority(priority: CasePriority) {
    if (priority === item.priority) return;
    try {
      await changePriority.mutateAsync({ id: item.id, priority });
      toast({ variant: "success", description: t("service:priorityUpdated") });
    } catch (error) {
      fail(error);
    }
  }

  async function onAssign(assignedUserId: string | null) {
    if (assignedUserId === (item.assignedUserId ?? null)) return;
    try {
      await assign.mutateAsync({ id: item.id, assignedUserId });
      toast({ variant: "success", description: t("service:assigneeUpdated") });
    } catch (error) {
      fail(error);
    }
  }

  return (
    <>
      <Menu position="bottom-end" withinPortal>
        <Menu.Target>
          <Button
            variant="default"
            rightSection={<ChevronDown size={16} />}
            disabled={targets.length === 0 || changeStatus.isPending}
          >
            {t("service:actions.status")}
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          {targets.map((to) => (
            <Menu.Item key={to} onClick={() => choose(to)}>
              {t(`service:transitions.${transitionKey(item.status, to)}`)}
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>

      <Select
        aria-label={t("service:fields.priority")}
        w={130}
        data={CASE_PRIORITIES.map((v) => ({ value: v, label: t(`service:priorities.${v}`) }))}
        value={item.priority}
        onChange={(value) => value && void onPriority(value as CasePriority)}
        allowDeselect={false}
        disabled={!active || changePriority.isPending}
      />
      <AssigneeSelect
        aria-label={t("service:fields.assignee")}
        w={180}
        value={item.assignedUserId ?? null}
        currentUserName={item.assignedUserName}
        onChange={(userId) => void onAssign(userId)}
        disabled={!active || assign.isPending}
      />

      <Button
        variant="default"
        leftSection={<Pencil size={16} />}
        onClick={onEdit}
        disabled={!active}
      >
        {t("common:edit")}
      </Button>
      <Button
        variant="default"
        color="red"
        leftSection={<Trash2 size={16} />}
        onClick={onDelete}
      >
        {t("common:delete")}
      </Button>

      {pending && (
        <ResolutionDialog
          from={item.status}
          to={pending}
          loading={changeStatus.isPending}
          error={noteError}
          onSubmit={(note) => void move(pending, note)}
          onClose={() => setPending(null)}
        />
      )}
    </>
  );
}
