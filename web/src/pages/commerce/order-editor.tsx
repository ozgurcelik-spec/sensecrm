import { Navigate, useParams } from "react-router";
import { Skeleton, Stack } from "@mantine/core";
import { DocumentEditor } from "@/components/commerce/document-editor";
import { useEditorPrefill } from "@/components/commerce/use-editor-prefill";
import { LoadError } from "@/components/load-error";
import { useOrder } from "@/hooks/use-orders";
import { getApiErrorStatus } from "@/lib/api-error";

function EditorSkeleton() {
  return (
    <Stack gap="sm">
      <Skeleton h={36} w={320} />
      <Skeleton h={320} />
    </Stack>
  );
}

function NewOrder() {
  const { ready, prefill } = useEditorPrefill();
  if (!ready) return <EditorSkeleton />;
  return <DocumentEditor kind="order" prefill={prefill} />;
}

function EditOrder({ id }: { id: string }) {
  const { data: order, isLoading, error, refetch } = useOrder(id);
  if (error) {
    return getApiErrorStatus(error) === 404 ? (
      <Navigate to="/app/orders" replace />
    ) : (
      <LoadError error={error} onRetry={() => void refetch()} />
    );
  }
  if (isLoading || !order) return <EditorSkeleton />;
  // Only drafts are editable; anything else shows the read-only detail page.
  if (order.status !== "draft") return <Navigate to={`/app/orders/${id}`} replace />;
  return <DocumentEditor kind="order" existing={order} />;
}

/** `/app/orders/new` (direct order, optionally prefilled) and `/app/orders/:id/edit`. */
export default function OrderEditorPage() {
  const { id } = useParams<{ id: string }>();
  return id ? <EditOrder id={id} /> : <NewOrder />;
}
