import { Navigate, useParams } from "react-router";
import { Skeleton, Stack } from "@mantine/core";
import { DocumentEditor } from "@/components/commerce/document-editor";
import { useEditorPrefill } from "@/components/commerce/use-editor-prefill";
import { LoadError } from "@/components/load-error";
import { useQuote } from "@/hooks/use-quotes";
import { getApiErrorStatus } from "@/lib/api-error";

function EditorSkeleton() {
  return (
    <Stack gap="sm">
      <Skeleton h={36} w={320} />
      <Skeleton h={320} />
    </Stack>
  );
}

function NewQuote() {
  const { ready, prefill } = useEditorPrefill();
  if (!ready) return <EditorSkeleton />;
  return <DocumentEditor kind="quote" prefill={prefill} />;
}

function EditQuote({ id }: { id: string }) {
  const { data: quote, isLoading, error, refetch } = useQuote(id);
  if (error) {
    return getApiErrorStatus(error) === 404 ? (
      <Navigate to="/app/quotes" replace />
    ) : (
      <LoadError error={error} onRetry={() => void refetch()} />
    );
  }
  if (isLoading || !quote) return <EditorSkeleton />;
  // Only drafts are editable; anything else shows the read-only detail page.
  if (quote.status !== "draft") return <Navigate to={`/app/quotes/${id}`} replace />;
  return <DocumentEditor kind="quote" existing={quote} />;
}

/** `/app/quotes/new` (optionally prefilled from `?accountId&contactId&dealId`) and `/app/quotes/:id/edit`. */
export default function QuoteEditorPage() {
  const { id } = useParams<{ id: string }>();
  return id ? <EditQuote id={id} /> : <NewQuote />;
}
