import { useState } from "react";
import { useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Badge, Button, Card, NumberInput, Stack, Text } from "@mantine/core";
import { Pencil, Star, Trash2 } from "lucide-react";
import { PriceBookEntries } from "@/components/commerce/pricebook-entries";
import { PriceBookFormDialog } from "@/components/commerce/pricebook-form-dialog";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { LookupDialog } from "@/components/lookup/lookup-dialog";
import { useAccountSource } from "@/components/lookup/lookup-sources";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useDeletePriceBook, usePriceBook, useSetAccountDefaultPriceBook } from "@/hooks/use-pricebooks";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { flatPrice } from "@/lib/pricebook";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS, type PriceBook } from "@/types";

/** "Örnek çözüm": what a catalog price becomes under a `flat` book (display only; the server resolves the real price). */
function FlatPreview({ priceBook }: { priceBook: PriceBook }) {
  const { t } = useTranslation(["inventory"]);
  const [catalog, setCatalog] = useState<number | string>(100);
  const resolved = flatPrice(catalog, priceBook.adjustmentPercent ?? 0);
  return (
    <Card withBorder padding="md" maw={420}>
      <Text fw={600} mb="xs">
        {t("inventory:priceBooks.preview.title")}
      </Text>
      <Text size="sm" c="dimmed" mb="sm">
        {t("inventory:priceBooks.preview.hint", { percent: priceBook.adjustmentPercent ?? 0 })}
      </Text>
      <NumberInput
        label={t("inventory:priceBooks.preview.catalogPrice")}
        hideControls
        min={0}
        decimalScale={4}
        value={catalog}
        onChange={setCatalog}
        mb="sm"
      />
      <Text size="sm">
        {t("inventory:priceBooks.preview.result")}:{" "}
        <strong data-testid="flat-preview">{resolved === null ? "-" : formatMoney(resolved, priceBook.currency)}</strong>
      </Text>
    </Card>
  );
}

export default function PriceBookDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["inventory", "common", "crm"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWritePriceBooks } = useCrmPermissions();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const { data: priceBook, isLoading, error, refetch } = usePriceBook(id);
  const remove = useDeletePriceBook();
  const setDefault = useSetAccountDefaultPriceBook();
  const accountSource = useAccountSource();
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [pickingAccount, setPickingAccount] = useState(false);

  async function confirmDelete() {
    if (!priceBook) return;
    try {
      await remove.mutateAsync(priceBook.id);
      toast({ variant: "success", description: t("inventory:priceBooks.deleted") });
      navigate("/app/pricebooks", { replace: true });
    } catch (err) {
      toastApiError(err);
      setDeleting(false);
    }
  }

  const tabs = priceBook
    ? [
        {
          value: "general",
          label: t("inventory:priceBooks.tabs.prices"),
          content:
            priceBook.pricingModel === "perProduct" ? (
              <PriceBookEntries priceBook={priceBook} canWrite={canWritePriceBooks} />
            ) : (
              <Stack gap="md">
                <Alert color="blue" variant="light">
                  {t("inventory:priceBooks.flatNote", { percent: priceBook.adjustmentPercent ?? 0 })}
                </Alert>
                <FlatPreview priceBook={priceBook} />
              </Stack>
            ),
        },
        {
          value: "audit",
          label: t("crm:tabs.audit"),
          content: <RecordAuditTab entityType="PriceBook" entityId={priceBook.id} />,
        },
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/pricebooks"
        backLabel={t("inventory:priceBooks.title")}
        title={priceBook?.name}
        subtitle={priceBook ? t(`inventory:priceBooks.models.${priceBook.pricingModel}`) : undefined}
        badges={
          priceBook && (
            <Badge variant="light" color={priceBook.isEffective ? "green" : priceBook.isActive ? "orange" : "gray"}>
              {priceBook.isEffective
                ? t("inventory:priceBooks.state.effective")
                : priceBook.isActive
                  ? t("inventory:priceBooks.state.outOfRange")
                  : t("inventory:priceBooks.state.inactive")}
            </Badge>
          )
        }
        actions={
          priceBook &&
          canWritePriceBooks && (
            <>
              {canReadAccounts && (
                <Button
                  leftSection={<Star size={16} />}
                  loading={setDefault.isPending}
                  onClick={() => setPickingAccount(true)}
                >
                  {t("inventory:priceBooks.makeDefault")}
                </Button>
              )}
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
          priceBook && (
            <InfoPanel
              title={t("crm:panel.details")}
              rows={[
                {
                  label: t("inventory:priceBooks.fields.pricingModel"),
                  value:
                    priceBook.pricingModel === "flat" && priceBook.adjustmentPercent !== undefined
                      ? `${t("inventory:priceBooks.models.flat")} (${priceBook.adjustmentPercent > 0 ? "+" : ""}${priceBook.adjustmentPercent}%)`
                      : t(`inventory:priceBooks.models.${priceBook.pricingModel}`),
                },
                { label: t("inventory:priceBooks.fields.currency"), value: priceBook.currency },
                {
                  label: t("inventory:priceBooks.fields.validity"),
                  value:
                    priceBook.validFrom || priceBook.validTo
                      ? `${formatCalendarDate(priceBook.validFrom)} - ${formatCalendarDate(priceBook.validTo)}`
                      : t("inventory:priceBooks.noValidity"),
                },
                { label: t("inventory:priceBooks.fields.entryCount"), value: priceBook.entryCount },
                { label: t("inventory:priceBooks.fields.description"), value: orDash(priceBook.description) },
                { label: t("crm:owner"), value: orDash(priceBook.ownerName) },
                { label: t("crm:createdAt"), value: formatDateTime(priceBook.createdAt, timeZone) },
                ...(priceBook.updatedAt
                  ? [{ label: t("crm:updatedAt"), value: formatDateTime(priceBook.updatedAt, timeZone) }]
                  : []),
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {editing && priceBook && <PriceBookFormDialog priceBook={priceBook} onClose={() => setEditing(false)} />}
      {priceBook && (
        <LookupDialog
          opened={pickingAccount}
          onClose={() => setPickingAccount(false)}
          title={t("inventory:priceBooks.makeDefaultTitle", { name: priceBook.name })}
          source={accountSource}
          onSelect={(account) =>
            setDefault.mutate(
              { accountId: account.id, priceBookId: priceBook.id },
              {
                onSuccess: () =>
                  toast({
                    variant: "success",
                    description: t("inventory:priceBooks.defaultSet", { account: account.name }),
                  }),
                // commerce.related_not_found: the account is gone or not visible.
                onError: (err) => toastApiError(err),
              }
            )
          }
        />
      )}
      <ConfirmDialog
        opened={deleting}
        title={t("inventory:priceBooks.deleteTitle")}
        message={t("inventory:priceBooks.deleteMessage", { name: priceBook?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(false)}
      />
    </>
  );
}
