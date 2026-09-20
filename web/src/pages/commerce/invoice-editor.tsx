import { Navigate, useParams } from "react-router";
import { Skeleton, Stack } from "@mantine/core";
import { DocumentEditor } from "@/components/commerce/document-editor";
import { useEditorPrefill } from "@/components/commerce/use-editor-prefill";
import { LoadError } from "@/components/load-error";
import { useInvoice } from "@/hooks/use-invoices";
import { getApiErrorStatus } from "@/lib/api-error";

function EditorSkeleton() {
  return (
    <Stack gap="sm">
      <Skeleton h={36} w={320} />
      <Skeleton h={320} />
    </Stack>
  );
}

function NewInvoice() {
  const { ready, prefill } = useEditorPrefill();
  if (!ready) return <EditorSkeleton />;
  return <DocumentEditor kind="invoice" prefill={prefill} />;
}

function EditInvoice({ id }: { id: string }) {
  const { data: invoice, isLoading, error, refetch } = useInvoice(id);
  if (error) {
    return getApiErrorStatus(error) === 404 ? (
      <Navigate to="/app/invoices" replace />
    ) : (
      <LoadError error={error} onRetry={() => void refetch()} />
    );
  }
  if (isLoading || !invoice) return <EditorSkeleton />;
  // Only drafts are editable; anything else shows the read-only detail page.
  if (invoice.status !== "draft") return <Navigate to={`/app/invoices/${id}`} replace />;
  return <DocumentEditor kind="invoice" existing={invoice} />;
}

/** `/app/invoices/new` (optionally prefilled from `?accountId&contactId&dealId`) and `/app/invoices/:id/edit`. */
export default function InvoiceEditorPage() {
  const { id } = useParams<{ id: string }>();
  return id ? <EditInvoice id={id} /> : <NewInvoice />;
}
