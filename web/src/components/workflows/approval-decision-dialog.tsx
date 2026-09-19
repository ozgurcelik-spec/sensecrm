import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Modal, SegmentedControl, Stack, Text, Textarea } from "@mantine/core";
import { useDecideApproval } from "@/hooks/use-approvals";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import { formatMoney } from "@/lib/format";
import { APPROVAL_DECISIONS, type Approval, type ApprovalDecision } from "@/types";

interface ApprovalDecisionDialogProps {
  approval: Approval;
  /** Decision preselected by the row button that opened the dialog. */
  initialDecision: ApprovalDecision;
  onClose: () => void;
}

/**
 * Approve / reject dialog. A rejection needs a comment (checked here and by the server, whose
 * `comment` field error is shown under the field). `approval.already_decided` (409) means somebody
 * decided first: the user gets a friendly message and the lists are refetched (by the mutation).
 */
export function ApprovalDecisionDialog({
  approval,
  initialDecision,
  onClose,
}: ApprovalDecisionDialogProps) {
  const { t } = useTranslation(["workflows", "common"]);
  const decide = useDecideApproval();
  const [decision, setDecision] = useState<ApprovalDecision>(initialDecision);
  const [comment, setComment] = useState("");
  const [error, setError] = useState<string | undefined>();

  const trimmed = comment.trim();
  const reject = decision === "reject";

  async function submit() {
    if (reject && !trimmed) {
      setError(t("workflows:approvals.dialog.commentRequired"));
      return;
    }
    try {
      await decide.mutateAsync({ id: approval.id, decision, comment: trimmed || undefined });
      toast({
        variant: "success",
        description: reject ? t("workflows:approvals.rejected") : t("workflows:approvals.approved"),
      });
      onClose();
    } catch (err) {
      const problem = getApiProblem(err);
      if (problem?.code === "approval.already_decided") {
        toast({ description: t("workflows:approvals.alreadyDecided") });
        onClose();
        return;
      }
      const fieldError = problem?.errors?.["comment"]?.[0] ?? problem?.errors?.["Comment"]?.[0];
      if (fieldError) setError(fieldError);
      else toastApiError(err);
    }
  }

  return (
    <Modal opened onClose={onClose} title={t("workflows:approvals.dialog.title")} centered>
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <Stack gap="md">
          <div>
            <Text fw={600}>{approval.title}</Text>
            {(approval.subjectName || approval.amount !== undefined) && (
              <Text size="sm" c="dimmed">
                {[
                  approval.subjectName,
                  approval.amount !== undefined
                    ? formatMoney(approval.amount, approval.currency)
                    : undefined,
                ]
                  .filter(Boolean)
                  .join(" · ")}
              </Text>
            )}
          </div>
          <SegmentedControl
            fullWidth
            aria-label={t("workflows:approvals.dialog.decision")}
            value={decision}
            onChange={(value) => {
              setDecision(value as ApprovalDecision);
              setError(undefined);
            }}
            data={APPROVAL_DECISIONS.map((d) => ({
              value: d,
              label: t(`workflows:approvals.${d}`),
            }))}
          />
          <Textarea
            label={t("workflows:approvals.dialog.comment")}
            description={reject ? undefined : t("workflows:approvals.dialog.commentOptional")}
            withAsterisk={reject}
            autosize
            minRows={3}
            maxRows={8}
            data-autofocus
            value={comment}
            onChange={(event) => {
              setComment(event.currentTarget.value);
              setError(undefined);
            }}
            error={error}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose} disabled={decide.isPending}>
              {t("common:cancel")}
            </Button>
            <Button type="submit" color={reject ? "red" : "green"} loading={decide.isPending}>
              {reject
                ? t("workflows:approvals.dialog.submitReject")
                : t("workflows:approvals.dialog.submitApprove")}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
