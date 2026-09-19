import { useTranslation } from "react-i18next";
import { ActionIcon, Checkbox, Tooltip } from "@mantine/core";
import { Check, RotateCcw } from "lucide-react";
import { useSetActivityStatus } from "@/hooks/use-activities";
import { hasStatusAction } from "@/lib/activity";
import type { Activity } from "@/types";

/**
 * Complete / reopen icon button of an activity row. Renders nothing for notes (their status is
 * fixed). The list updates optimistically; a failed request restores it and shows an error toast.
 */
export function ActivityStatusButton({ activity }: { activity: Activity }) {
  const { t } = useTranslation(["activities"]);
  const setStatus = useSetActivityStatus();
  if (!hasStatusAction(activity)) return null;
  const action = activity.status === "open" ? "complete" : "reopen";
  const label = t(`activities:actions.${action}`);
  return (
    <Tooltip label={label}>
      <ActionIcon
        variant="subtle"
        color={action === "complete" ? "green" : "gray"}
        aria-label={label}
        onClick={() => setStatus.mutate({ id: activity.id, action })}
      >
        {action === "complete" ? <Check size={16} /> : <RotateCcw size={16} />}
      </ActionIcon>
    </Tooltip>
  );
}

/** Checkbox variant (dashboard task list): checked = completed; ticking it completes the task. */
export function ActivityCheckbox({
  activity,
  disabled = false,
  label,
}: {
  activity: Activity;
  disabled?: boolean;
  label: string;
}) {
  const setStatus = useSetActivityStatus();
  return (
    <Checkbox
      aria-label={label}
      checked={activity.status !== "open"}
      disabled={disabled || setStatus.isPending || !hasStatusAction(activity)}
      onChange={(event) =>
        setStatus.mutate({
          id: activity.id,
          action: event.currentTarget.checked ? "complete" : "reopen",
        })
      }
    />
  );
}
