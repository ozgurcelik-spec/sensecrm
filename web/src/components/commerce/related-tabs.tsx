/** "Teklifler" / "Siparişler" tabs of the account and deal detail pages. */
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Skeleton, Table, Text } from "@mantine/core";
import { LoadError } from "@/components/load-error";
import { useInvoices } from "@/hooks/use-invoices";
import { useOrders } from "@/hooks/use-orders";
import { useQuotes } from "@/hooks/use-quotes";
import { formatCalendarDate, formatMoney } from "@/lib/format";
import { InvoiceStatusBadge, OrderStatusBadge, QuoteStatusBadge } from "./status-badges";

/** Related list of at most this many documents; the full list is on the module's own page. */
const RELATED_LIMIT = 50;

export function RelatedQuotesTab({ accountId, dealId }: { accountId?: string; dealId?: string }) {
  const { t } = useTranslation(["commerce"]);
  const { data, isLoading, error, refetch } = useQuotes({
    page: 1,
    pageSize: RELATED_LIMIT,
    accountId,
    dealId,
    sort: "-createdAt",
  });

  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading) return <Skeleton h={64} />;
  if (!data || data.items.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="lg">
        {t("commerce:related.noQuotes")}
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
            <Table.Th>{t("commerce:fields.validUntil")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {data.items.map((q) => (
            <Table.Tr key={q.id}>
              <Table.Td>
                <Anchor component={Link} to={`/app/quotes/${q.id}`} size="sm">
                  {q.number}
                </Anchor>
              </Table.Td>
              <Table.Td>{q.subject}</Table.Td>
              <Table.Td>
                <QuoteStatusBadge status={q.status} />
              </Table.Td>
              <Table.Td ta="right">{formatMoney(q.grandTotal, q.currency)}</Table.Td>
              <Table.Td>{formatCalendarDate(q.validUntil)}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

export function RelatedOrdersTab({ accountId }: { accountId: string }) {
  const { t } = useTranslation(["commerce"]);
  const { data, isLoading, error, refetch } = useOrders({
    page: 1,
    pageSize: RELATED_LIMIT,
    accountId,
    sort: "-createdAt",
  });

  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading) return <Skeleton h={64} />;
  if (!data || data.items.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="lg">
        {t("commerce:related.noOrders")}
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
            <Table.Th>{t("commerce:fields.orderDate")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {data.items.map((o) => (
            <Table.Tr key={o.id}>
              <Table.Td>
                <Anchor component={Link} to={`/app/orders/${o.id}`} size="sm">
                  {o.number}
                </Anchor>
              </Table.Td>
              <Table.Td>{o.subject}</Table.Td>
              <Table.Td>
                <OrderStatusBadge status={o.status} />
              </Table.Td>
              <Table.Td ta="right">{formatMoney(o.grandTotal, o.currency)}</Table.Td>
              <Table.Td>{formatCalendarDate(o.orderDate)}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/** "Faturalar" tab of the account and deal detail pages (`crm.invoices.read`; `GET /invoices?accountId=|dealId=`). */
export function RelatedInvoicesTab({ accountId, dealId }: { accountId?: string; dealId?: string }) {
  const { t } = useTranslation(["commerce", "invoices"]);
  const { data, isLoading, error, refetch } = useInvoices({
    page: 1,
    pageSize: RELATED_LIMIT,
    accountId,
    dealId,
    sort: "-createdAt",
  });

  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading) return <Skeleton h={64} />;
  if (!data || data.items.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="lg">
        {t("invoices:related.noInvoices")}
      </Text>
    );
  }
  return (
    <Table.ScrollContainer minWidth={640}>
      <Table verticalSpacing="xs" highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t("commerce:fields.number")}</Table.Th>
            <Table.Th>{t("commerce:fields.subject")}</Table.Th>
            <Table.Th>{t("commerce:fields.status")}</Table.Th>
            <Table.Th ta="right">{t("commerce:totals.grandTotal")}</Table.Th>
            <Table.Th ta="right">{t("invoices:fields.balance")}</Table.Th>
            <Table.Th>{t("invoices:fields.dueDate")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {data.items.map((invoice) => (
            <Table.Tr key={invoice.id}>
              <Table.Td>
                <Anchor component={Link} to={`/app/invoices/${invoice.id}`} size="sm">
                  {invoice.number}
                </Anchor>
              </Table.Td>
              <Table.Td>{invoice.subject}</Table.Td>
              <Table.Td>
                <InvoiceStatusBadge status={invoice.status} />
              </Table.Td>
              <Table.Td ta="right">{formatMoney(invoice.grandTotal, invoice.currency)}</Table.Td>
              <Table.Td ta="right">{formatMoney(invoice.balanceAmount, invoice.currency)}</Table.Td>
              <Table.Td>{formatCalendarDate(invoice.dueDate)}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
