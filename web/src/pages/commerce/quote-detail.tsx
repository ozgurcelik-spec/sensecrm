import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Card, Stack, Text } from "@mantine/core";
import {
  ArrowRightLeft,
  Check,
  Clock,
  Pencil,
  RotateCcw,
  Send,
  Trash2,
  X,
} from "lucide-react";
import { ExtendDialog, ReasonDialog } from "@/components/commerce/action-dialogs";
import { LinesTable, TermsAndNotes, TotalsCard } from "@/components/commerce/document-parts";
import { QuoteStatusBadge } from "@/components/commerce/status-badges";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { useAttachmentsTab } from "@/hooks/use-attachments-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { usePermission } from "@/hooks/use-permission";
import { useConvertQuote, useDeleteQuote, useQuote, useQuoteAction } from "@/hooks/use-quotes";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import { availableQuoteActions, type QuoteActionKey } from "@/lib/commerce-actions";
import { formatDateTime } from "@/lib/dates";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { formatYmd, ymdInZone } from "@/lib/zoned-time";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS } from "@/types";

type Dialog = "reject" | "extend" | "delete" | null;

export default function QuoteDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["commerce", "common", "crm"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteQuotes, canWriteOrders } = useCrmPermissions();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const canReadDeals = usePermission(PERMISSIONS.crmDealsRead);
  const canReadOrders = usePermission(PERMISSIONS.crmOrdersRead);
  const { data: quote, isLoading, error, refetch } = useQuote(id);
  const action = useQuoteAction();
  const convert = useConvertQuote();
  const remove = useDeleteQuote();
  const [dialog, setDialog] = useState<Dialog>(null);
  const [extendError, setExtendError] = useState<string | undefined>();

  const actions: QuoteActionKey[] = quote
    ? availableQuoteActions(quote.status, {
        canWriteQuotes,
        canWriteOrders,
        converted: !!quote.convertedOrderId,
      })
    : [];
  const today = formatYmd(ymdInZone(new Date(), timeZone));

  /** Runs a state action; the hook refetches on success and on failure (a 409 means the quote changed). */
  function run(
    key: "send" | "accept" | "revert" | "reject" | "extend",
    extra: { reason?: string; validUntil?: string } = {}
  ) {
    if (!quote) return;
    action.mutate(
      { id: quote.id, action: key, ...extra },
      {
        onSuccess: () => {
          setDialog(null);
          toast({ variant: "success", description: t(`commerce:quotes.done.${key}`) });
        },
        onError: (err) => {
          const validUntilError = getApiProblem(err)?.errors?.validUntil?.[0];
          if (key === "extend" && validUntilError) {
            setExtendError(validUntilError);
            return;
          }
          setDialog(null);
          toastApiError(err);
        },
      }
    );
  }

  async function convertToOrder() {
    if (!quote) return;
    try {
      const order = await convert.mutateAsync(quote.id);
      toast({
        variant: "success",
        description: t("commerce:quotes.done.convert", { number: order.number }),
      });
      navigate(`/app/orders/${order.id}`);
    } catch (err) {
      // quote.not_accepted / quote.already_converted / commerce.related_not_found / ...
      toastApiError(err);
    }
  }

  async function confirmDelete() {
    if (!quote) return;
    try {
      await remove.mutateAsync(quote.id);
      toast({ variant: "success", description: t("commerce:quotes.deleted") });
      navigate("/app/quotes", { replace: true });
    } catch (err) {
      toastApiError(err);
      setDialog(null);
    }
  }

  const busy = action.isPending || convert.isPending;

  const attachmentsTab = useAttachmentsTab("quote", id);
  const tabs = quote
    ? [
        {
          value: "general",
          label: t("crm:tabs.general"),
          content: (
            <Stack gap="md">
              <Card withBorder padding="md">
                <Text fw={600} mb="sm">
                  {t("commerce:lines.title")}
                </Text>
                <LinesTable lines={quote.lines} currency={quote.currency} />
              </Card>
              <TotalsCard totals={quote} currency={quote.currency} />
              <TermsAndNotes terms={quote.terms} notes={quote.notes} />
            </Stack>
          ),
        },
        ...attachmentsTab,
        {
          value: "audit",
          label: t("crm:tabs.audit"),
          content: <RecordAuditTab entityType="Quote" entityId={quote.id} />,
        },
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/quotes"
        backLabel={t("commerce:quotes.title")}
        title={quote ? `${quote.number} - ${quote.subject}` : undefined}
        subtitle={quote?.accountName}
        badges={quote && <QuoteStatusBadge status={quote.status} />}
        actions={
          quote && (
            <>
              {actions.includes("accept") && (
                <Button
                  leftSection={<Check size={16} />}
                  loading={action.isPending && action.variables?.action === "accept"}
                  disabled={busy}
                  onClick={() => run("accept")}
                >
                  {t("commerce:actions.accept")}
                </Button>
              )}
              {actions.includes("send") && (
                <Button
                  leftSection={<Send size={16} />}
                  loading={action.isPending && action.variables?.action === "send"}
                  disabled={busy}
                  onClick={() => run("send")}
                >
                  {t("commerce:actions.send")}
                </Button>
              )}
              {actions.includes("convert") && (
                <Button
                  leftSection={<ArrowRightLeft size={16} />}
                  loading={convert.isPending}
                  disabled={busy}
                  onClick={() => void convertToOrder()}
                >
                  {t("commerce:actions.convert")}
                </Button>
              )}
              {actions.includes("extend") && (
                <Button
                  variant="default"
                  leftSection={<Clock size={16} />}
                  disabled={busy}
                  onClick={() => {
                    setExtendError(undefined);
                    setDialog("extend");
                  }}
                >
                  {t("commerce:actions.extend")}
                </Button>
              )}
              {actions.includes("reject") && (
                <Button
                  variant="default"
                  color="red"
                  leftSection={<X size={16} />}
                  disabled={busy}
                  onClick={() => setDialog("reject")}
                >
                  {t("commerce:actions.reject")}
                </Button>
              )}
              {actions.includes("revert") && (
                <Button
                  variant="default"
                  leftSection={<RotateCcw size={16} />}
                  loading={action.isPending && action.variables?.action === "revert"}
                  disabled={busy}
                  onClick={() => run("revert")}
                >
                  {t("commerce:actions.revert")}
                </Button>
              )}
              {actions.includes("edit") && (
                <Button
                  variant="default"
                  leftSection={<Pencil size={16} />}
                  onClick={() => navigate(`/app/quotes/${quote.id}/edit`)}
                >
                  {t("common:edit")}
                </Button>
              )}
              {actions.includes("delete") && (
                <Button
                  variant="default"
                  color="red"
                  leftSection={<Trash2 size={16} />}
                  onClick={() => setDialog("delete")}
                >
                  {t("common:delete")}
                </Button>
              )}
            </>
          )
        }
        isLoading={isLoading}
        error={error}
        onRetry={() => void refetch()}
        panel={
          quote && (
            <InfoPanel
              title={t("crm:panel.details")}
              rows={[
                {
                  label: t("commerce:fields.account"),
                  value: canReadAccounts ? (
                    <Anchor component={Link} to={`/app/accounts/${quote.accountId}`} size="sm">
                      {quote.accountName ?? quote.accountId}
                    </Anchor>
                  ) : (
                    orDash(quote.accountName)
                  ),
                },
                {
                  label: t("commerce:fields.contact"),
                  value:
                    quote.contactId && canReadContacts ? (
                      <Anchor component={Link} to={`/app/contacts/${quote.contactId}`} size="sm">
                        {quote.contactName ?? quote.contactId}
                      </Anchor>
                    ) : (
                      orDash(quote.contactName)
                    ),
                },
                {
                  label: t("commerce:fields.deal"),
                  value:
                    quote.dealId && canReadDeals ? (
                      <Anchor component={Link} to={`/app/deals/${quote.dealId}`} size="sm">
                        {quote.dealName ?? quote.dealId}
                      </Anchor>
                    ) : (
                      orDash(quote.dealName)
                    ),
                },
                {
                  label: t("commerce:fields.validUntil"),
                  value: quote.validUntil
                    ? formatCalendarDate(quote.validUntil)
                    : t("commerce:quotes.noExpiry"),
                },
                { label: t("commerce:fields.currency"), value: quote.currency },
                {
                  label: t("commerce:totals.grandTotal"),
                  value: formatMoney(quote.grandTotal, quote.currency),
                },
                { label: t("crm:owner"), value: orDash(quote.ownerName) },
                ...(quote.convertedOrderId
                  ? [
                      {
                        label: t("commerce:fields.order"),
                        value: canReadOrders ? (
                          <Anchor
                            component={Link}
                            to={`/app/orders/${quote.convertedOrderId}`}
                            size="sm"
                          >
                            {t("commerce:quotes.orderLink", {
                              number: quote.convertedOrderNumber ?? quote.convertedOrderId,
                            })}
                          </Anchor>
                        ) : (
                          (quote.convertedOrderNumber ?? quote.convertedOrderId)
                        ),
                      },
                    ]
                  : []),
                ...(quote.sentAt
                  ? [{ label: t("commerce:fields.sentAt"), value: formatDateTime(quote.sentAt, timeZone) }]
                  : []),
                ...(quote.acceptedAt
                  ? [
                      {
                        label: t("commerce:fields.acceptedAt"),
                        value: formatDateTime(quote.acceptedAt, timeZone),
                      },
                    ]
                  : []),
                ...(quote.rejectedAt
                  ? [
                      {
                        label: t("commerce:fields.rejectedAt"),
                        value: formatDateTime(quote.rejectedAt, timeZone),
                      },
                      {
                        label: t("commerce:fields.rejectionReason"),
                        value: orDash(quote.rejectionReason),
                      },
                    ]
                  : []),
                { label: t("crm:createdAt"), value: formatDateTime(quote.createdAt, timeZone) },
                ...(quote.updatedAt
                  ? [{ label: t("crm:updatedAt"), value: formatDateTime(quote.updatedAt, timeZone) }]
                  : []),
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {dialog === "reject" && quote && (
        <ReasonDialog
          title={t("commerce:dialogs.rejectTitle")}
          label={t("commerce:dialogs.rejectReason")}
          confirmLabel={t("commerce:actions.reject")}
          loading={action.isPending}
          onConfirm={(reason) => run("reject", { reason })}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === "extend" && quote && (
        <ExtendDialog
          minDate={today}
          initialDate={quote.validUntil?.slice(0, 10)}
          loading={action.isPending}
          serverError={extendError}
          onConfirm={(validUntil) => {
            setExtendError(undefined);
            run("extend", { validUntil });
          }}
          onClose={() => setDialog(null)}
        />
      )}
      <ConfirmDialog
        opened={dialog === "delete"}
        title={t("commerce:quotes.deleteTitle")}
        message={t("commerce:quotes.deleteMessage", { number: quote?.number })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDialog(null)}
      />
    </>
  );
}
