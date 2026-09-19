import { useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import {
  Alert,
  Button,
  Card,
  Group,
  SegmentedControl,
  Stack,
  Text,
  Textarea,
} from "@mantine/core";
import { useAddCaseComment } from "@/hooks/use-cases";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors, getApiProblem } from "@/lib/api-error";
import type { CaseStatus, CommentVisibility } from "@/types";

const MAX_BODY = 8000;

interface CaseReplyBoxProps {
  caseId: string;
  status: CaseStatus;
  /** Called after a server rejection that means the case changed under us (closed, conflict). */
  onStale?: () => void;
}

/**
 * Reply box of a case: "public reply" vs "internal note" is an explicit choice with no default (a
 * wrongly public comment is worse than one extra click), the send button stays disabled until one is
 * picked and text is entered. Ctrl/Cmd+Enter sends. A closed case shows a banner instead.
 */
export function CaseReplyBox({ caseId, status, onStale }: CaseReplyBoxProps) {
  const { t } = useTranslation(["service", "common"]);
  const add = useAddCaseComment();
  const [visibility, setVisibility] = useState<CommentVisibility | null>(null);
  const [body, setBody] = useState("");
  const [error, setError] = useState<string | undefined>();

  if (status === "closed") {
    return (
      <Alert color="gray" variant="light" role="status">
        {t("service:reply.closedBanner")}
      </Alert>
    );
  }

  const trimmed = body.trim();
  const canSend = !!visibility && trimmed.length > 0 && trimmed.length <= MAX_BODY;

  async function send() {
    if (!visibility || !canSend || add.isPending) return;
    setError(undefined);
    try {
      await add.mutateAsync({ id: caseId, visibility, body: trimmed });
      setBody("");
      // Back to "no choice": the next comment is a conscious public / internal decision again.
      setVisibility(null);
      toast({ variant: "success", description: t("service:reply.sent") });
    } catch (err) {
      const code = getApiProblem(err)?.code;
      if (code === "case.closed") {
        setError(t("common:errors.case.closed"));
        onStale?.();
      } else if (
        !applyValidationErrors<{ body: string }>(
          err,
          (_field, { message }) => setError(message),
          ["body"]
        )
      ) {
        toastApiError(err);
        if (getApiProblem(err)?.status === 409) onStale?.();
      }
    }
  }

  function onKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      void send();
    }
  }

  return (
    <Card withBorder padding="sm" data-testid="reply-box">
      <Stack gap="sm">
        <SegmentedControl
          aria-label={t("service:reply.visibilityLabel")}
          // "" matches no option: nothing is selected until the agent chooses.
          value={visibility ?? ""}
          onChange={(value) => setVisibility(value as CommentVisibility)}
          data={[
            { value: "public", label: t("service:reply.public") },
            { value: "internal", label: t("service:reply.internal") },
          ]}
        />
        <Textarea
          aria-label={t("service:reply.body")}
          placeholder={
            visibility === "internal"
              ? t("service:reply.placeholderInternal")
              : visibility === "public"
                ? t("service:reply.placeholderPublic")
                : t("service:reply.placeholder")
          }
          autosize
          minRows={3}
          maxRows={10}
          value={body}
          onChange={(event) => setBody(event.currentTarget.value)}
          onKeyDown={onKeyDown}
          error={error}
        />
        <Group justify="space-between" align="center">
          <Text size="xs" c="dimmed">
            {t("service:reply.hint")}
          </Text>
          <Button onClick={() => void send()} disabled={!canSend} loading={add.isPending}>
            {t("service:reply.send")}
          </Button>
        </Group>
      </Stack>
    </Card>
  );
}
