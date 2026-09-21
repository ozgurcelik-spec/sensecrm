/**
 * Lookup sources of the M9C plan: one per entity, each wrapping the entity's existing paged list hook
 * (no new server endpoint). `useXSource()` returns a memoized source whose column headers follow the
 * UI language.
 */
import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Badge } from "@mantine/core";
import { useAccounts } from "@/hooks/use-accounts";
import { useContacts } from "@/hooks/use-contacts";
import { useDeals } from "@/hooks/use-deals";
import { useOrders } from "@/hooks/use-orders";
import { usePriceBooks } from "@/hooks/use-pricebooks";
import { useProducts } from "@/hooks/use-products";
import { useQuotes } from "@/hooks/use-quotes";
import { useVendors } from "@/hooks/use-vendors";
import { formatCalendarDate, formatMoney, formatNumber, orDash } from "@/lib/format";
import type {
  Account,
  Contact,
  Deal,
  OrderSummary,
  PriceBook,
  Product,
  QuoteSummary,
  Vendor,
} from "@/types";
import type { LookupFilters, LookupSearchParams, LookupSearchResult, LookupSource } from "./lookup-types";

function text(filters: LookupFilters | undefined, key: string): string | undefined {
  const value = filters?.[key];
  return value === undefined || value === "" ? undefined : String(value);
}

function result<T>(query: {
  data?: { items: T[]; totalCount: number };
  isFetching: boolean;
  isError: boolean;
}): LookupSearchResult<T> {
  return {
    items: query.data?.items ?? [],
    totalCount: query.data?.totalCount ?? 0,
    isFetching: query.isFetching,
    isError: query.isError,
  };
}

const base = (p: LookupSearchParams) => ({ page: p.page, pageSize: p.pageSize, q: p.q, sort: p.sort });

function useAccountSearch(p: LookupSearchParams) {
  return result(useAccounts({ ...base(p), ownerUserId: text(p.filters, "ownerUserId") }));
}

function useContactSearch(p: LookupSearchParams) {
  return result(useContacts({ ...base(p), accountId: text(p.filters, "accountId") }));
}

function useDealSearch(p: LookupSearchParams) {
  return result(useDeals({ ...base(p), accountId: text(p.filters, "accountId") }));
}

function useProductSearch(p: LookupSearchParams) {
  return result(
    useProducts({
      ...base(p),
      isActive: text(p.filters, "isActive") ?? true,
      currency: text(p.filters, "currency"),
      vendorId: text(p.filters, "vendorId"),
    })
  );
}

function useVendorSearch(p: LookupSearchParams) {
  return result(useVendors({ ...base(p), category: text(p.filters, "category") }));
}

function usePriceBookSearch(p: LookupSearchParams) {
  return result(
    usePriceBooks({
      ...base(p),
      isActive: text(p.filters, "isActive"),
      effective: text(p.filters, "effective"),
      currency: text(p.filters, "currency"),
    })
  );
}

function useOrderSearch(p: LookupSearchParams) {
  return result(
    useOrders({ ...base(p), accountId: text(p.filters, "accountId"), status: text(p.filters, "status") })
  );
}

function useQuoteSearch(p: LookupSearchParams) {
  return result(
    useQuotes({ ...base(p), accountId: text(p.filters, "accountId"), status: text(p.filters, "status") })
  );
}

export function useAccountSource(): LookupSource<Account> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({
      queryKey: "accounts",
      useSearch: useAccountSearch,
      getId: (a) => a.id,
      getLabel: (a) => a.name,
      defaultSort: "name",
      columns: [
        { key: "name", header: t("inventory:lookup.columns.name"), sortField: "name", render: (a) => a.name },
        { key: "industry", header: t("inventory:lookup.columns.industry"), render: (a) => orDash(a.industry) },
        { key: "phone", header: t("inventory:lookup.columns.phone"), render: (a) => orDash(a.phone) },
        { key: "owner", header: t("inventory:lookup.columns.owner"), render: (a) => orDash(a.ownerName) },
      ],
    }),
    [t]
  );
}

export function useContactSource(): LookupSource<Contact> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({
      queryKey: "contacts",
      useSearch: useContactSearch,
      getId: (c) => c.id,
      getLabel: (c) => c.fullName,
      defaultSort: "lastName",
      columns: [
        { key: "name", header: t("inventory:lookup.columns.name"), sortField: "lastName", render: (c) => c.fullName },
        { key: "account", header: t("inventory:lookup.columns.account"), render: (c) => orDash(c.accountName) },
        { key: "email", header: t("inventory:lookup.columns.email"), render: (c) => orDash(c.email) },
        { key: "phone", header: t("inventory:lookup.columns.phone"), render: (c) => orDash(c.phone) },
      ],
    }),
    [t]
  );
}

export function useDealSource(): LookupSource<Deal> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({
      queryKey: "deals",
      useSearch: useDealSearch,
      getId: (d) => d.id,
      getLabel: (d) => d.name,
      columns: [
        { key: "name", header: t("inventory:lookup.columns.name"), sortField: "name", render: (d) => d.name },
        {
          key: "amount",
          header: t("inventory:lookup.columns.amount"),
          sortField: "amount",
          render: (d) => (d.amount === undefined ? "-" : formatMoney(d.amount, d.currency)),
        },
        { key: "stage", header: t("inventory:lookup.columns.stage"), render: (d) => d.stageName },
        {
          key: "closingDate",
          header: t("inventory:lookup.columns.closingDate"),
          sortField: "closingDate",
          render: (d) => formatCalendarDate(d.closingDate),
        },
        { key: "account", header: t("inventory:lookup.columns.account"), render: (d) => d.accountName },
        { key: "contact", header: t("inventory:lookup.columns.contact"), render: (d) => orDash(d.contactName) },
        { key: "owner", header: t("inventory:lookup.columns.owner"), render: (d) => orDash(d.ownerName) },
      ],
    }),
    [t]
  );
}

export function useProductSource(): LookupSource<Product> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({
      queryKey: "products",
      useSearch: useProductSearch,
      getId: (p) => p.id,
      getLabel: (p) => p.name,
      defaultSort: "name",
      columns: [
        { key: "name", header: t("inventory:lookup.columns.name"), sortField: "name", render: (p) => p.name },
        { key: "code", header: t("inventory:lookup.columns.code"), sortField: "code", render: (p) => orDash(p.code) },
        {
          key: "unitPrice",
          header: t("inventory:lookup.columns.unitPrice"),
          sortField: "unitPrice",
          render: (p) => formatMoney(p.unitPrice, p.currency),
        },
        { key: "taxRate", header: t("inventory:lookup.columns.taxRate"), render: (p) => `${formatNumber(p.taxRate)}%` },
        { key: "unit", header: t("inventory:lookup.columns.unit"), render: (p) => orDash(p.unit) },
      ],
    }),
    [t]
  );
}

export function useVendorSource(): LookupSource<Vendor> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({
      queryKey: "vendors",
      useSearch: useVendorSearch,
      getId: (v) => v.id,
      getLabel: (v) => v.name,
      defaultSort: "name",
      columns: [
        { key: "name", header: t("inventory:lookup.columns.name"), sortField: "name", render: (v) => v.name },
        { key: "category", header: t("inventory:lookup.columns.category"), sortField: "category", render: (v) => orDash(v.category) },
        { key: "phone", header: t("inventory:lookup.columns.phone"), render: (v) => orDash(v.phone) },
        { key: "email", header: t("inventory:lookup.columns.email"), render: (v) => orDash(v.email) },
      ],
    }),
    [t]
  );
}

export function usePriceBookSource(): LookupSource<PriceBook> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({
      queryKey: "pricebooks",
      useSearch: usePriceBookSearch,
      getId: (b) => b.id,
      getLabel: (b) => b.name,
      defaultSort: "name",
      columns: [
        { key: "name", header: t("inventory:lookup.columns.name"), sortField: "name", render: (b) => b.name },
        {
          key: "model",
          header: t("inventory:lookup.columns.model"),
          render: (b) => (
            <Badge variant="light" color="gray">
              {t(`inventory:priceBooks.models.${b.pricingModel}`)}
            </Badge>
          ),
        },
        { key: "currency", header: t("inventory:lookup.columns.currency"), render: (b) => b.currency },
        {
          key: "validity",
          header: t("inventory:lookup.columns.validity"),
          sortField: "validTo",
          render: (b) =>
            b.validFrom || b.validTo
              ? `${formatCalendarDate(b.validFrom)} - ${formatCalendarDate(b.validTo)}`
              : t("inventory:priceBooks.noValidity"),
        },
      ],
    }),
    [t]
  );
}

export function useOrderSource(): LookupSource<OrderSummary> {
  const { t } = useTranslation(["inventory", "commerce"]);
  return useMemo(
    () => ({
      queryKey: "orders",
      useSearch: useOrderSearch,
      getId: (o) => o.id,
      getLabel: (o) => `${o.number} - ${o.subject}`,
      defaultSort: "-createdAt",
      columns: [
        { key: "number", header: t("inventory:lookup.columns.number"), sortField: "number", render: (o) => o.number },
        { key: "subject", header: t("inventory:lookup.columns.subject"), sortField: "subject", render: (o) => o.subject },
        { key: "account", header: t("inventory:lookup.columns.account"), render: (o) => orDash(o.accountName) },
        { key: "status", header: t("inventory:lookup.columns.status"), render: (o) => t(`commerce:orderStatuses.${o.status}`) },
        { key: "total", header: t("inventory:lookup.columns.total"), sortField: "grandTotal", render: (o) => formatMoney(o.grandTotal, o.currency) },
      ],
    }),
    [t]
  );
}

export function useQuoteSource(): LookupSource<QuoteSummary> {
  const { t } = useTranslation(["inventory", "commerce"]);
  return useMemo(
    () => ({
      queryKey: "quotes",
      useSearch: useQuoteSearch,
      getId: (q) => q.id,
      getLabel: (q) => `${q.number} - ${q.subject}`,
      defaultSort: "-createdAt",
      columns: [
        { key: "number", header: t("inventory:lookup.columns.number"), sortField: "number", render: (q) => q.number },
        { key: "subject", header: t("inventory:lookup.columns.subject"), sortField: "subject", render: (q) => q.subject },
        { key: "account", header: t("inventory:lookup.columns.account"), render: (q) => orDash(q.accountName) },
        { key: "status", header: t("inventory:lookup.columns.status"), render: (q) => t(`commerce:quoteStatuses.${q.status}`) },
        { key: "total", header: t("inventory:lookup.columns.total"), sortField: "grandTotal", render: (q) => formatMoney(q.grandTotal, q.currency) },
      ],
    }),
    [t]
  );
}
