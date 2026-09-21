import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Badge, Button, Card, Skeleton, Stack, Table, Text } from "@mantine/core";
import { Pencil, Trash2 } from "lucide-react";
import { DocumentAddresses } from "@/components/commerce/document-parts";
import { PurchaseOrderStatusBadge } from "@/components/commerce/status-badges";
import { VendorFormDialog } from "@/components/commerce/vendor-form-dialog";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { LoadError } from "@/components/load-error";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { usePermission } from "@/hooks/use-permission";
import { useProducts } from "@/hooks/use-products";
import { usePurchaseOrders } from "@/hooks/use-purchase-orders";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useDeleteVendor, useVendor } from "@/hooks/use-vendors";
import { formatDateTime } from "@/lib/dates";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { safeHttpHref } from "@/lib/url";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS } from "@/types";

const RELATED_LIMIT = 50;

function VendorProducts({ vendorId }: { vendorId: string }) {
  const { t } = useTranslation(["inventory"]);
  const { data, isLoading, error, refetch } = useProducts({ page: 1, pageSize: RELATED_LIMIT, vendorId });
  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading) return <Skeleton h={64} />;
  if (!data || data.items.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="lg">
        {t("inventory:vendors.noProducts")}
      </Text>
    );
  }
  return (
    <Table.ScrollContainer minWidth={520}>
      <Table verticalSpacing="xs" highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t("inventory:lookup.columns.name")}</Table.Th>
            <Table.Th>{t("inventory:lookup.columns.code")}</Table.Th>
            <Table.Th ta="right">{t("inventory:products.purchasePrice")}</Table.Th>
            <Table.Th ta="right">{t("inventory:lookup.columns.unitPrice")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {data.items.map((product) => (
            <Table.Tr key={product.id}>
              <Table.Td>{product.name}</Table.Td>
              <Table.Td>{orDash(product.code)}</Table.Td>
              <Table.Td ta="right">
                {product.purchasePrice === undefined ? "-" : formatMoney(product.purchasePrice, product.currency)}
              </Table.Td>
              <Table.Td ta="right">{formatMoney(product.unitPrice, product.currency)}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

function VendorPurchaseOrders({ vendorId }: { vendorId: string }) {
  const { t } = useTranslation(["inventory", "commerce"]);
  const { data, isLoading, error, refetch } = usePurchaseOrders({
    page: 1,
    pageSize: RELATED_LIMIT,
    vendorId,
    sort: "-createdAt",
  });
  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading) return <Skeleton h={64} />;
  if (!data || data.items.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="lg">
        {t("inventory:vendors.noPurchaseOrders")}
      </Text>
    );
  }
  return (
    <Table.ScrollContainer minWidth={560}>
      <Table verticalSpacing="xs" highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t("commerce:fields.number")}</Table.Th>
            <Table.Th>{t("commerce:fields.subject")}</Table.Th>
            <Table.Th>{t("commerce:fields.status")}</Table.Th>
            <Table.Th ta="right">{t("commerce:totals.grandTotal")}</Table.Th>
            <Table.Th>{t("inventory:purchaseOrders.fields.poDate")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {data.items.map((po) => (
            <Table.Tr key={po.id}>
              <Table.Td>
                <Anchor component={Link} to={`/app/purchase-orders/${po.id}`} size="sm">
                  {po.number}
                </Anchor>
              </Table.Td>
              <Table.Td>{po.subject}</Table.Td>
              <Table.Td>
                <PurchaseOrderStatusBadge status={po.status} />
              </Table.Td>
              <Table.Td ta="right">{formatMoney(po.grandTotal, po.currency)}</Table.Td>
              <Table.Td>{formatCalendarDate(po.poDate)}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

export default function VendorDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["inventory", "common", "crm"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteVendors } = useCrmPermissions();
  const canReadProducts = usePermission(PERMISSIONS.crmProductsRead);
  const canReadPurchaseOrders = usePermission(PERMISSIONS.crmPurchaseOrdersRead);
  const { data: vendor, isLoading, error, refetch } = useVendor(id);
  const remove = useDeleteVendor();
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);

  async function confirmDelete() {
    if (!vendor) return;
    try {
      await remove.mutateAsync(vendor.id);
      toast({ variant: "success", description: t("inventory:vendors.deleted") });
      navigate("/app/vendors", { replace: true });
    } catch (err) {
      // vendor.in_use: a purchase order still points to the vendor.
      toastApiError(err);
      setDeleting(false);
    }
  }

  const website = safeHttpHref(vendor?.website);

  const tabs = vendor
    ? [
        {
          value: "general",
          label: t("crm:tabs.general"),
          content: (
            <Stack gap="md">
              <DocumentAddresses billingAddress={vendor.address} singleTitle={t("inventory:vendors.fields.address")} />
              <Card withBorder padding="md">
                <Text fw={600} mb="sm">
                  {t("inventory:vendors.fields.description")}
                </Text>
                <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
                  {orDash(vendor.description)}
                </Text>
              </Card>
            </Stack>
          ),
        },
        ...(canReadProducts
          ? [
              {
                value: "products",
                label: t("inventory:vendors.tabs.products", { count: vendor.productCount }),
                content: <VendorProducts vendorId={vendor.id} />,
              },
            ]
          : []),
        ...(canReadPurchaseOrders
          ? [
              {
                value: "purchaseOrders",
                label: t("inventory:vendors.tabs.purchaseOrders", { count: vendor.purchaseOrderCount }),
                content: <VendorPurchaseOrders vendorId={vendor.id} />,
              },
            ]
          : []),
        {
          value: "audit",
          label: t("crm:tabs.audit"),
          content: <RecordAuditTab entityType="Vendor" entityId={vendor.id} />,
        },
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/vendors"
        backLabel={t("inventory:vendors.title")}
        title={vendor?.name}
        subtitle={vendor?.category}
        badges={
          vendor?.emailOptOut ? (
            <Badge variant="light" color="orange">
              {t("inventory:vendors.emailOptOutBadge")}
            </Badge>
          ) : undefined
        }
        actions={
          vendor &&
          canWriteVendors && (
            <>
              <Button variant="default" leftSection={<Pencil size={16} />} onClick={() => setEditing(true)}>
                {t("common:edit")}
              </Button>
              <Button variant="default" color="red" leftSection={<Trash2 size={16} />} onClick={() => setDeleting(true)}>
                {t("common:delete")}
              </Button>
            </>
          )
        }
        isLoading={isLoading}
        error={error}
        onRetry={() => void refetch()}
        panel={
          vendor && (
            <InfoPanel
              title={t("crm:panel.details")}
              rows={[
                { label: t("inventory:vendors.fields.phone"), value: orDash(vendor.phone) },
                { label: t("inventory:vendors.fields.email"), value: orDash(vendor.email) },
                {
                  label: t("inventory:vendors.fields.website"),
                  value: website ? (
                    <Anchor href={website} target="_blank" rel="noopener noreferrer" size="sm">
                      {vendor.website}
                    </Anchor>
                  ) : (
                    orDash(vendor.website)
                  ),
                },
                { label: t("inventory:vendors.fields.category"), value: orDash(vendor.category) },
                { label: t("inventory:vendors.fields.glAccount"), value: orDash(vendor.glAccount) },
                { label: t("crm:owner"), value: orDash(vendor.ownerName) },
                { label: t("crm:createdAt"), value: formatDateTime(vendor.createdAt, timeZone) },
                ...(vendor.updatedAt
                  ? [{ label: t("crm:updatedAt"), value: formatDateTime(vendor.updatedAt, timeZone) }]
                  : []),
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {editing && vendor && <VendorFormDialog vendor={vendor} onClose={() => setEditing(false)} />}
      <ConfirmDialog
        opened={deleting}
        title={t("inventory:vendors.deleteTitle")}
        message={t("inventory:vendors.deleteMessage", { name: vendor?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(false)}
      />
    </>
  );
}
