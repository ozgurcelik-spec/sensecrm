import { Navigate, useParams } from "react-router";
import { Skeleton, Stack } from "@mantine/core";
import { DocumentEditor } from "@/components/commerce/document-editor";
import { LoadError } from "@/components/load-error";
import { usePurchaseOrder } from "@/hooks/use-purchase-orders";
import { getApiErrorStatus } from "@/lib/api-error";

function EditorSkeleton() {
  return (
    <Stack gap="sm">
      <Skeleton h={36} w={320} />
      <Skeleton h={320} />
    </Stack>
  );
}

function EditPurchaseOrder({ id }: { id: string }) {
  const { data: order, isLoading, error, refetch } = usePurchaseOrder(id);
  if (error) {
    return getApiErrorStatus(error) === 404 ? (
      <Navigate to="/app/purchase-orders" replace />
    ) : (
      <LoadError error={error} onRetry={() => void refetch()} />
    );
  }
  if (isLoading || !order) return <EditorSkeleton />;
  // Only drafts are editable; anything else shows the read-only detail page.
  if (order.status !== "draft") return <Navigate to={`/app/purchase-orders/${id}`} replace />;
  return <DocumentEditor kind="purchaseOrder" existing={order} />;
}

/** `/app/purchase-orders/new` and `/app/purchase-orders/:id/edit`. */
export default function PurchaseOrderEditorPage() {
  const { id } = useParams<{ id: string }>();
  return id ? <EditPurchaseOrder id={id} /> : <DocumentEditor kind="purchaseOrder" />;
}
