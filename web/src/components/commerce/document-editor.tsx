import { useEffect, useRef, useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Controller, useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import {
  Alert,
  Anchor,
  Autocomplete,
  Button,
  Card,
  Group,
  NumberInput,
  Select,
  SimpleGrid,
  Stack,
  Text,
  Textarea,
  TextInput,
} from "@mantine/core";
import { ArrowLeft } from "lucide-react";
import { OwnerSelect } from "@/components/crm/owner-select";
import { PageHeader } from "@/components/page-header";
import {
  useAccountLookupCreate,
  useContactLookupCreate,
  useDealLookupCreate,
  usePriceBookLookupCreate,
  useVendorLookupCreate,
} from "@/components/lookup/lookup-creates";
import { LookupField } from "@/components/lookup/lookup-field";
import {
  useAccountSource,
  useContactSource,
  useDealSource,
  usePriceBookSource,
  useVendorSource,
} from "@/components/lookup/lookup-sources";
import type { LookupValue } from "@/components/lookup/lookup-types";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { usePermission } from "@/hooks/use-permission";
import { useSaveInvoice } from "@/hooks/use-invoices";
import { useSaveOrder } from "@/hooks/use-orders";
import { useSavePurchaseOrder } from "@/hooks/use-purchase-orders";
import { useSaveQuote } from "@/hooks/use-quotes";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import {
  checkAdjustment,
  toSignedMinor,
} from "@/lib/commerce-totals";
import {
  hasLineErrors,
  linesFromServer,
  newLine,
  splitLineErrors,
  toLineInputs,
  validateLines,
  type LineDraft,
  type LineErrors,
} from "@/lib/commerce-lines";
import {
  DOCUMENT_FIELD_LAYOUT,
  DOCUMENT_KINDS,
  currencySuffix,
  hasPriceBook,
  type DocumentField,
  type DocumentKind,
} from "@/lib/document-layout";
import {
  toDocumentAddressPayload,
  toDocumentAddressValues,
  type AddressField,
  type DocumentAddressValues,
} from "@/lib/document-address";
import { blankToUndefined } from "@/lib/format";
import { getAccountDefaultPriceBook } from "@/services/pricebooks.service";
import { formatYmd, ymdInZone } from "@/lib/zoned-time";
import { useAuthStore } from "@/store/auth.store";
import {
  CURRENCIES,
  PERMISSIONS,
  type DocumentAddress,
  type DocumentLine,
  type Invoice,
  type PurchaseOrder,
  type Quote,
  type SalesOrder,
} from "@/types";
import { AddressBlocks, type AddressErrors } from "./address-blocks";
import { LineItemsGrid } from "./line-items-grid";

export type { DocumentKind } from "@/lib/document-layout";

/** Values a new document starts with (the "Teklif oluştur" actions of a deal / account). */
export interface EditorPrefill {
  accountId?: string;
  accountName?: string;
  contactId?: string;
  contactName?: string;
  dealId?: string;
  dealName?: string;
  currency?: string;
  subject?: string;
}

/** The fields of a saved document the editor reads; every kind fills its own subset. */
interface ExistingDocument {
  id: string;
  number: string;
  subject: string;
  accountId?: string;
  accountName?: string;
  vendorId?: string;
  vendorName?: string;
  contactId?: string;
  contactName?: string;
  dealId?: string;
  dealName?: string;
  ownerUserId: string;
  ownerName?: string;
  currency: string;
  validUntil?: string;
  orderDate?: string;
  invoiceDate?: string;
  poDate?: string;
  dueDate?: string;
  customerPoNumber?: string;
  exciseTax?: number;
  salesCommission?: number;
  pending?: string;
  carrier?: string;
  adjustment?: number;
  billingAddress?: DocumentAddress;
  shippingAddress?: DocumentAddress;
  priceBookId?: string;
  priceBookName?: string;
  terms?: string;
  notes?: string;
  lines: DocumentLine[];
}

const numberish = z.union([z.number(), z.string()]);

function toNumber(value: number | string): number {
  if (typeof value === "number") return value;
  return value.trim() === "" ? Number.NaN : Number(value);
}

function isBlank(value: number | string): boolean {
  return typeof value === "string" && value.trim() === "";
}

function decimalPlaces(value: number): number {
  const text = value.toString();
  return text.includes("e") ? Number.POSITIVE_INFINITY : (text.split(".")[1]?.length ?? 0);
}

const schema = z
  .object({
    subject: z.string().trim().min(1, "auth:validation.required").max(200, "commerce:validation.subjectMax"),
    accountId: z.string(),
    vendorId: z.string(),
    contactId: z.string(),
    dealId: z.string(),
    validUntil: z.string(),
    orderDate: z.string(),
    invoiceDate: z.string(),
    poDate: z.string(),
    dueDate: z.string(),
    customerPoNumber: z.string().trim().max(64, "commerce:validation.customerPoMax"),
    exciseTax: numberish,
    salesCommission: numberish,
    pending: z.string().trim().max(100, "commerce:validation.pendingMax"),
    carrier: z.string().trim().max(64, "commerce:validation.carrierMax"),
    priceBookId: z.string(),
    ownerUserId: z.string(),
    currency: z.string().min(1, "auth:validation.required"),
    terms: z.string().max(4000, "commerce:validation.termsMax"),
    notes: z.string().max(2000, "commerce:validation.notesMax"),
  })
  .superRefine((values, ctx) => {
    for (const field of ["exciseTax", "salesCommission"] as const) {
      const raw = values[field];
      if (isBlank(raw)) continue;
      const amount = toNumber(raw);
      if (!Number.isFinite(amount) || amount < 0 || amount > 1_000_000_000) {
        ctx.addIssue({ code: "custom", path: [field], message: "commerce:validation.amountRange" });
      } else if (decimalPlaces(amount) > 2) {
        ctx.addIssue({ code: "custom", path: [field], message: "commerce:validation.decimals2" });
      }
    }
  });

type FormValues = z.infer<typeof schema>;

const HEADER_FIELDS = [
  "subject",
  "accountId",
  "vendorId",
  "contactId",
  "dealId",
  "validUntil",
  "orderDate",
  "invoiceDate",
  "poDate",
  "dueDate",
  "customerPoNumber",
  "exciseTax",
  "salesCommission",
  "pending",
  "carrier",
  "priceBookId",
  "ownerUserId",
  "currency",
  "terms",
  "notes",
] as const;

const DEFAULT_VALIDITY_DAYS = 30;

/** Today plus `days` in the organization's calendar, as `YYYY-MM-DD`. */
function todayPlus(days: number, timeZone: string | undefined): string {
  const today = ymdInZone(new Date(), timeZone);
  const shifted = new Date(Date.UTC(today.year, today.month - 1, today.day + days));
  return formatYmd({
    year: shifted.getUTCFullYear(),
    month: shifted.getUTCMonth() + 1,
    day: shifted.getUTCDate(),
  });
}

/** Common carriers offered as suggestions (free text, at most 64 characters: the server stores a label only). */
const CARRIER_SUGGESTIONS = ["Yurtiçi Kargo", "Aras Kargo", "MNG Kargo", "PTT Kargo", "UPS", "DHL", "FedEx"];

interface DocumentEditorProps {
  kind: DocumentKind;
  /** The document being edited (must be a draft); omitted when creating. */
  existing?: Quote | SalesOrder | Invoice | PurchaseOrder;
  prefill?: EditorPrefill;
}

type Errors = Partial<Record<AddressField, string>>;

/** Server `billingAddress.city` style keys -> per block field errors. */
function splitAddressErrors(errors: Record<string, string[]>): {
  billing: Errors;
  shipping: Errors;
  rest: Record<string, string[]>;
} {
  const billing: Errors = {};
  const shipping: Errors = {};
  const rest: Record<string, string[]> = {};
  for (const [key, messages] of Object.entries(errors)) {
    const match = /^(billing|shipping)Address\.(\w+)$/i.exec(key);
    if (match?.[1] && match[2] && messages[0]) {
      const field = (match[2].charAt(0).toLowerCase() + match[2].slice(1)) as AddressField;
      (match[1].toLowerCase() === "billing" ? billing : shipping)[field] = messages[0];
    } else {
      rest[key] = messages;
    }
  }
  return { billing, shipping, rest };
}

/**
 * Create / edit form of a quote, order, invoice or purchase order: header fields (which ones comes from
 * `DOCUMENT_FIELD_LAYOUT`), the two address blocks, the shared line item grid with its rounding line
 * and live total preview, terms and notes. Saves the contract body only (no computed amounts, the
 * unit price always explicit), maps server field errors (`lines[i].field` onto grid cells,
 * `billingAddress.*` onto the address inputs) and shows the saved document's page afterwards.
 */
export function DocumentEditor({ kind, existing: existingDocument, prefill }: DocumentEditorProps) {
  const { t } = useTranslation(["commerce", "common", "auth", "crm", "inventory", "invoices"]);
  const navigate = useNavigate();
  const config = DOCUMENT_KINDS[kind];
  const layout = DOCUMENT_FIELD_LAYOUT[kind];
  const existing = existingDocument as ExistingDocument | undefined;
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const defaultOwnerId = useDefaultOwnerId();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const canReadDeals = usePermission(PERMISSIONS.crmDealsRead);
  const canReadProducts = usePermission(PERMISSIONS.crmProductsRead);
  const canReadVendors = usePermission(PERMISSIONS.crmVendorsRead);
  const canReadPriceBooks = usePermission(PERMISSIONS.crmPriceBooksRead);
  const accountSource = useAccountSource();
  const contactSource = useContactSource();
  const dealSource = useDealSource();
  const vendorSource = useVendorSource();
  const priceBookSource = usePriceBookSource();
  const accountCreate = useAccountLookupCreate();
  const contactCreate = useContactLookupCreate();
  const dealCreate = useDealLookupCreate();
  const vendorCreate = useVendorLookupCreate();
  const priceBookCreate = usePriceBookLookupCreate();
  const saveQuote = useSaveQuote();
  const saveOrder = useSaveOrder();
  const saveInvoice = useSaveInvoice();
  const savePurchaseOrder = useSavePurchaseOrder();
  const saving =
    saveQuote.isPending || saveOrder.isPending || saveInvoice.isPending || savePurchaseOrder.isPending;
  const purchase = kind === "purchaseOrder";
  const withPriceBook = hasPriceBook(kind);

  const [lines, setLines] = useState<LineDraft[]>(() =>
    existing ? linesFromServer(existing.lines) : [newLine()]
  );
  const [lineErrors, setLineErrors] = useState<LineErrors>({});
  const [formErrors, setFormErrors] = useState<string[]>([]);
  const [adjustment, setAdjustment] = useState<number | string>(existing?.adjustment ?? 0);
  const [adjustmentError, setAdjustmentError] = useState<string | undefined>();
  const [billing, setBilling] = useState<DocumentAddressValues>(() =>
    toDocumentAddressValues(existing?.billingAddress)
  );
  const [shipping, setShipping] = useState<DocumentAddressValues>(() =>
    toDocumentAddressValues(existing?.shippingAddress)
  );
  const [billingErrors, setBillingErrors] = useState<Errors>({});
  const [shippingErrors, setShippingErrors] = useState<Errors>({});
  // Labels of the lookup picks (a pick need not be in any searched list, so the id alone is not enough).
  const [accountLabel, setAccountLabel] = useState(existing?.accountName ?? prefill?.accountName);
  const [vendorLabel, setVendorLabel] = useState(existing?.vendorName);
  const [contactLabel, setContactLabel] = useState(existing?.contactName ?? prefill?.contactName);
  const [dealLabel, setDealLabel] = useState(existing?.dealName ?? prefill?.dealName);
  const [priceBook, setPriceBook] = useState<{ label?: string; currency?: string; suggested?: boolean }>({
    label: existing?.priceBookName,
  });

  const {
    register,
    control,
    handleSubmit,
    setError,
    setValue,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      subject: existing?.subject ?? prefill?.subject ?? "",
      accountId: existing?.accountId ?? prefill?.accountId ?? "",
      vendorId: existing?.vendorId ?? "",
      contactId: existing?.contactId ?? prefill?.contactId ?? "",
      dealId: existing?.dealId ?? prefill?.dealId ?? "",
      validUntil:
        existing?.validUntil?.slice(0, 10) ??
        (kind === "quote" && !existing ? todayPlus(DEFAULT_VALIDITY_DAYS, timeZone) : ""),
      orderDate: existing?.orderDate?.slice(0, 10) ?? "",
      invoiceDate: existing?.invoiceDate?.slice(0, 10) ?? (kind === "invoice" && !existing ? formatYmd(ymdInZone(new Date(), timeZone)) : ""),
      poDate: existing?.poDate?.slice(0, 10) ?? "",
      dueDate: existing?.dueDate?.slice(0, 10) ?? "",
      customerPoNumber: existing?.customerPoNumber ?? "",
      exciseTax: existing?.exciseTax ?? "",
      salesCommission: existing?.salesCommission ?? "",
      pending: existing?.pending ?? "",
      carrier: existing?.carrier ?? "",
      priceBookId: existing?.priceBookId ?? "",
      ownerUserId: existing?.ownerUserId ?? defaultOwnerId,
      currency: existing?.currency ?? prefill?.currency ?? "TRY",
      terms: existing?.terms ?? "",
      notes: existing?.notes ?? "",
    },
  });

  const accountId = useWatch({ control, name: "accountId" });
  const priceBookId = useWatch({ control, name: "priceBookId" });
  const currency = useWatch({ control, name: "currency" });

  const message = (key?: string) => (key ? t(key, { defaultValue: key }) : undefined);
  const amountSuffix = ` ${currencySuffix(currency || "TRY")}`;
  const lookupValue = (id: string, label?: string): LookupValue | null => (id ? { id, label: label ?? id } : null);

  /** The account's default price book, when it is effective, as the document's price book (a suggestion: the user may change it). */
  async function suggestPriceBook(forAccountId: string) {
    if (!withPriceBook || !canReadPriceBooks) return;
    try {
      const suggestion = await getAccountDefaultPriceBook(forAccountId);
      if (suggestion?.isEffective) {
        setValue("priceBookId", suggestion.priceBookId);
        setPriceBook({ label: suggestion.priceBookName, suggested: true });
      }
    } catch {
      // No suggestion is better than an error for something the user did not ask for.
    }
  }

  const suggestedForPrefill = useRef(false);
  useEffect(() => {
    if (existing || suggestedForPrefill.current || !prefill?.accountId) return;
    suggestedForPrefill.current = true;
    void suggestPriceBook(prefill.accountId);
    // Runs once for the account a new document starts with.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function changeLines(next: LineDraft[]) {
    setLines(next);
    // A server / client error refers to what the user just changed: drop it instead of showing stale text.
    setLineErrors({});
  }

  /** Client-side line checks; returns true when the lines can be sent. */
  function checkLines(): boolean {
    const found = validateLines(lines);
    setLineErrors(found);
    return !hasLineErrors(found);
  }

  /** Adjustment rules of plan D3 (A4, A5, A7, A8); returns true when the value can be sent. */
  function checkAdjustmentValue(): boolean {
    const issue = checkAdjustment(adjustment, lines);
    setAdjustmentError(issue ? t(`commerce:validation.adjustment.${issue}`) : undefined);
    return !issue;
  }

  function checkDueDate(values: FormValues): boolean {
    const dateField = config.dateField;
    const documentDate = dateField ? values[dateField] : "";
    if (values.dueDate && documentDate && values.dueDate < documentDate) {
      setError("dueDate", { type: "client", message: "commerce:validation.dueBefore" });
      return false;
    }
    return true;
  }

  const onSubmit = handleSubmit(
    async (values) => {
      setFormErrors([]);
      setAdjustmentError(undefined);
      let valid = checkLines();
      if (!checkAdjustmentValue()) valid = false;
      if (!checkDueDate(values)) valid = false;
      if (purchase && !values.vendorId) {
        setError("vendorId", { type: "client", message: "auth:validation.required" });
        valid = false;
      }
      if (!purchase && !values.accountId) {
        setError("accountId", { type: "client", message: "auth:validation.required" });
        valid = false;
      }
      if (!valid) return;

      const optionalAmount = (raw: number | string) => (isBlank(raw) ? undefined : toNumber(raw));
      const common = {
        subject: values.subject.trim(),
        contactId: blankToUndefined(values.contactId),
        ownerUserId: blankToUndefined(values.ownerUserId),
        currency: values.currency.trim().toUpperCase(),
        terms: blankToUndefined(values.terms),
        notes: blankToUndefined(values.notes),
        carrier: blankToUndefined(values.carrier),
        // Always sent: PUT replaces the document and a missing value would reset it to 0 silently.
        adjustment: Number(toSignedMinor(adjustment) ?? 0n) / 100,
        billingAddress: toDocumentAddressPayload(billing),
        shippingAddress: toDocumentAddressPayload(shipping),
        lines: toLineInputs(lines),
      };
      const sales = {
        ...common,
        accountId: values.accountId,
        dealId: blankToUndefined(values.dealId),
        priceBookId: blankToUndefined(values.priceBookId),
      };
      const amounts = {
        exciseTax: optionalAmount(values.exciseTax),
        salesCommission: optionalAmount(values.salesCommission),
      };

      try {
        let id: string;
        if (kind === "quote") {
          id = await saveQuote.mutateAsync({ id: existing?.id, ...sales, validUntil: blankToUndefined(values.validUntil) });
        } else if (kind === "order") {
          id = await saveOrder.mutateAsync({
            id: existing?.id,
            ...sales,
            ...amounts,
            orderDate: blankToUndefined(values.orderDate),
            dueDate: blankToUndefined(values.dueDate),
            customerPoNumber: blankToUndefined(values.customerPoNumber),
            pending: blankToUndefined(values.pending),
          });
        } else if (kind === "invoice") {
          id = await saveInvoice.mutateAsync({
            id: existing?.id,
            ...sales,
            ...amounts,
            invoiceDate: blankToUndefined(values.invoiceDate),
            dueDate: blankToUndefined(values.dueDate),
            customerPoNumber: blankToUndefined(values.customerPoNumber),
          });
        } else {
          id = await savePurchaseOrder.mutateAsync({
            id: existing?.id,
            ...common,
            ...amounts,
            vendorId: values.vendorId,
            poDate: blankToUndefined(values.poDate),
            dueDate: blankToUndefined(values.dueDate),
          });
        }
        toast({
          variant: "success",
          description: t(`${config.ns}:${config.section}.${existing ? "updated" : "created"}`),
        });
        navigate(`${config.listPath}/${id}`);
      } catch (error) {
        handleSaveError(error);
      }
    },
    () => {
      checkLines();
      checkAdjustmentValue();
    }
  );

  function handleSaveError(error: unknown) {
    const problem = getApiProblem(error);
    if (!problem?.errors) {
      // commerce.related_not_found, *.not_editable, commerce.concurrent_update, ... : translated by code.
      toastApiError(error);
      return;
    }
    const { lineErrors: fromServer, rest: afterLines } = splitLineErrors(problem.errors);
    const { billing: billingFromServer, shipping: shippingFromServer, rest } = splitAddressErrors(afterLines);
    setLineErrors(fromServer);
    setBillingErrors(billingFromServer);
    setShippingErrors(shippingFromServer);
    const unmatched: string[] = [];
    let adjustmentMessage: string | undefined;
    for (const [rawField, messages] of Object.entries(rest)) {
      const field = rawField
        .split(".")
        .map((segment) => segment.charAt(0).toLowerCase() + segment.slice(1))
        .join(".");
      const target = HEADER_FIELDS.find((f) => f === field);
      if (field === "adjustment" && messages[0]) adjustmentMessage = messages[0];
      else if (target && messages[0]) setError(target, { type: "server", message: messages[0] });
      else unmatched.push(...messages);
    }
    setAdjustmentError(adjustmentMessage);
    setFormErrors(unmatched);
    const visible =
      Object.keys(billingFromServer).length + Object.keys(shippingFromServer).length > 0 || !!adjustmentMessage;
    // Errors that landed on a field or a cell are visible there; anything else needs a message.
    if (unmatched.length === 0 && !hasLineErrors(fromServer) && Object.keys(rest).length === 0 && !visible) {
      toastApiError(error);
    }
  }

  const title = t(`${config.ns}:${config.section}.${existing ? "editTitle" : "newTitle"}`, {
    number: existing?.number,
  });

  const money = (label: string, name: "exciseTax" | "salesCommission") => (
    <Controller
      key={name}
      control={control}
      name={name}
      render={({ field }) => (
        <NumberInput
          label={label}
          value={field.value}
          onChange={field.onChange}
          min={0}
          decimalScale={2}
          hideControls
          suffix={amountSuffix}
          error={message(errors[name]?.message)}
        />
      )}
    />
  );

  function renderField(key: DocumentField) {
    switch (key) {
      case "subject":
        return null; // the subject spans the full row, above the grid
      case "account":
        return (
          <Controller
            key={key}
            control={control}
            name="accountId"
            render={({ field }) => (
              <LookupField
                label={t("commerce:fields.account")}
                title={t("inventory:lookup.selectAccount")}
                source={accountSource}
                create={accountCreate}
                required
                readOnly={!canReadAccounts}
                value={lookupValue(field.value, accountLabel)}
                error={message(errors.accountId?.message)}
                onChange={(_row, picked) => {
                  field.onChange(picked?.id ?? "");
                  setAccountLabel(picked?.label);
                  // The contact and the deal belong to the previous account.
                  setValue("contactId", "");
                  setValue("dealId", "");
                  setContactLabel(undefined);
                  setDealLabel(undefined);
                  if (picked) void suggestPriceBook(picked.id);
                }}
              />
            )}
          />
        );
      case "vendor":
        return (
          <Controller
            key={key}
            control={control}
            name="vendorId"
            render={({ field }) => (
              <LookupField
                label={t("inventory:purchaseOrders.fields.vendor")}
                title={t("inventory:vendors.select")}
                source={vendorSource}
                create={vendorCreate}
                required
                readOnly={!canReadVendors}
                value={lookupValue(field.value, vendorLabel)}
                error={message(errors.vendorId?.message)}
                onChange={(_row, picked) => {
                  field.onChange(picked?.id ?? "");
                  setVendorLabel(picked?.label);
                }}
              />
            )}
          />
        );
      case "contact":
        if (!canReadContacts) return null;
        return (
          <Controller
            key={key}
            control={control}
            name="contactId"
            render={({ field }) => (
              <LookupField
                label={t("commerce:fields.contact")}
                title={t("inventory:lookup.selectContact")}
                source={contactSource}
                create={contactCreate}
                filters={purchase ? undefined : { accountId: accountId || undefined }}
                placeholder={!purchase && !accountId ? t("commerce:editor.needsAccount") : undefined}
                disabled={!purchase && !accountId}
                clearable
                value={lookupValue(field.value, contactLabel)}
                error={message(errors.contactId?.message)}
                onChange={(_row, picked) => {
                  field.onChange(picked?.id ?? "");
                  setContactLabel(picked?.label);
                }}
              />
            )}
          />
        );
      case "deal":
        if (!canReadDeals) return null;
        return (
          <Controller
            key={key}
            control={control}
            name="dealId"
            render={({ field }) => (
              <LookupField
                label={t("commerce:fields.deal")}
                title={t("inventory:lookup.selectDeal")}
                source={dealSource}
                create={dealCreate}
                filters={{ accountId: accountId || undefined }}
                placeholder={accountId ? undefined : t("commerce:editor.needsAccount")}
                disabled={!accountId}
                clearable
                value={lookupValue(field.value, dealLabel)}
                error={message(errors.dealId?.message)}
                onChange={(_row, picked) => {
                  field.onChange(picked?.id ?? "");
                  setDealLabel(picked?.label);
                }}
              />
            )}
          />
        );
      case "validUntil":
        return (
          <TextInput
            key={key}
            label={t("commerce:fields.validUntil")}
            type="date"
            error={message(errors.validUntil?.message)}
            {...register("validUntil")}
          />
        );
      case "orderDate":
        return (
          <TextInput
            key={key}
            label={t("commerce:fields.orderDate")}
            type="date"
            description={t("commerce:editor.orderDateHint")}
            error={message(errors.orderDate?.message)}
            {...register("orderDate")}
          />
        );
      case "invoiceDate":
        return (
          <TextInput
            key={key}
            label={t("invoices:fields.invoiceDate")}
            type="date"
            description={t("commerce:editor.orderDateHint")}
            error={message(errors.invoiceDate?.message)}
            {...register("invoiceDate")}
          />
        );
      case "poDate":
        return (
          <TextInput
            key={key}
            label={t("inventory:purchaseOrders.fields.poDate")}
            type="date"
            description={t("commerce:editor.orderDateHint")}
            error={message(errors.poDate?.message)}
            {...register("poDate")}
          />
        );
      case "dueDate":
        return (
          <TextInput
            key={key}
            label={t("commerce:fields.dueDate")}
            type="date"
            error={message(errors.dueDate?.message)}
            {...register("dueDate")}
          />
        );
      case "customerPoNumber":
        return (
          <TextInput
            key={key}
            label={t("commerce:fields.customerPoNumber")}
            error={message(errors.customerPoNumber?.message)}
            {...register("customerPoNumber")}
          />
        );
      case "exciseTax":
        return money(t("commerce:fields.exciseTax"), "exciseTax");
      case "salesCommission":
        return money(t("commerce:fields.salesCommission"), "salesCommission");
      case "pending":
        return (
          <TextInput
            key={key}
            label={t("commerce:fields.pending")}
            error={message(errors.pending?.message)}
            {...register("pending")}
          />
        );
      case "priceBook":
        if (!canReadPriceBooks) return null;
        return (
          <Controller
            key={key}
            control={control}
            name="priceBookId"
            render={({ field }) => (
              <LookupField
                label={t("commerce:fields.priceBook")}
                title={t("inventory:priceBooks.select")}
                source={priceBookSource}
                create={priceBookCreate}
                filters={{ effective: true, currency: currency || undefined }}
                clearable
                description={priceBook.suggested ? t("commerce:editor.priceBookSuggested") : undefined}
                value={lookupValue(field.value, priceBook.label)}
                error={message(errors.priceBookId?.message)}
                onChange={(row, picked) => {
                  field.onChange(picked?.id ?? "");
                  setPriceBook({ label: picked?.label, currency: row?.currency });
                }}
              />
            )}
          />
        );
      case "carrier":
        return (
          <Controller
            key={key}
            control={control}
            name="carrier"
            render={({ field }) => (
              // Free text (at most 64 characters) with the usual carriers as suggestions.
              <Autocomplete
                label={t("commerce:fields.carrier")}
                data={CARRIER_SUGGESTIONS}
                value={field.value}
                onChange={field.onChange}
                placeholder={t("commerce:editor.carrierPlaceholder")}
                error={message(errors.carrier?.message)}
              />
            )}
          />
        );
      case "owner":
        return (
          <Controller
            key={key}
            control={control}
            name="ownerUserId"
            render={({ field }) => (
              <OwnerSelect
                value={field.value || null}
                onChange={(value) => field.onChange(value ?? "")}
                currentOwnerId={existing?.ownerUserId}
                currentOwnerName={existing?.ownerName}
                error={message(errors.ownerUserId?.message)}
              />
            )}
          />
        );
      case "currency":
        return (
          <Controller
            key={key}
            control={control}
            name="currency"
            render={({ field }) => (
              <Select
                label={t("commerce:fields.currency")}
                data={[...CURRENCIES]}
                value={field.value}
                onChange={(value) => {
                  field.onChange(value ?? "TRY");
                  // A price book is fixed to one currency; a pick in another one no longer applies.
                  if (priceBook.currency && value && priceBook.currency !== value) {
                    setValue("priceBookId", "");
                    setPriceBook({});
                  }
                }}
                allowDeselect={false}
                error={message(errors.currency?.message)}
              />
            )}
          />
        );
    }
  }

  return (
    <form onSubmit={onSubmit} noValidate>
      <Stack gap="md">
        <Anchor
          component={Link}
          to={existing ? `${config.listPath}/${existing.id}` : config.listPath}
          size="sm"
        >
          <Group gap={4}>
            <ArrowLeft size={14} />
            {existing ? existing.number : t(`${config.ns}:${config.section}.title`)}
          </Group>
        </Anchor>
        <PageHeader title={title} />

        {formErrors.length > 0 && (
          <Alert color="red" variant="light" role="alert">
            {formErrors.map((text) => (
              <Text key={text} size="sm">
                {text}
              </Text>
            ))}
          </Alert>
        )}

        <Card withBorder padding="md">
          <Stack gap="md">
            <TextInput
              label={t("commerce:fields.subject")}
              withAsterisk
              data-autofocus
              error={message(errors.subject?.message)}
              {...register("subject")}
            />
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
              {layout.map((key) => renderField(key))}
            </SimpleGrid>
          </Stack>
        </Card>

        <Text fw={600}>{t("commerce:address.title")}</Text>
        <AddressBlocks
          billing={billing}
          shipping={shipping}
          onBillingChange={(values) => {
            setBilling(values);
            setBillingErrors({});
          }}
          onShippingChange={(values) => {
            setShipping(values);
            setShippingErrors({});
          }}
          billingErrors={billingErrors as AddressErrors}
          shippingErrors={shippingErrors as AddressErrors}
          accountId={purchase ? undefined : accountId || undefined}
          canReadAccounts={canReadAccounts && !purchase}
          disabled={saving}
        />

        <Card withBorder padding="md">
          <Text fw={600} mb="sm">
            {t("commerce:lines.title")}
          </Text>
          <LineItemsGrid
            lines={lines}
            onChange={changeLines}
            currency={currency || "TRY"}
            errors={lineErrors}
            canPickProducts={canReadProducts}
            disabled={saving}
            mode={purchase ? "purchase" : "sales"}
            priceBookId={withPriceBook && canReadPriceBooks && priceBookId ? priceBookId : undefined}
            adjustment={adjustment}
            onAdjustmentChange={(value) => {
              setAdjustment(value);
              setAdjustmentError(undefined);
            }}
            adjustmentError={adjustmentError}
          />
        </Card>

        <Card withBorder padding="md">
          <SimpleGrid cols={{ base: 1, md: 2 }} spacing="sm">
            <Textarea
              label={t("commerce:fields.terms")}
              autosize
              minRows={3}
              error={message(errors.terms?.message)}
              {...register("terms")}
            />
            <Textarea
              label={t("commerce:fields.notes")}
              autosize
              minRows={3}
              error={message(errors.notes?.message)}
              {...register("notes")}
            />
          </SimpleGrid>
        </Card>

        <Group justify="flex-end">
          <Button
            variant="default"
            onClick={() => navigate(existing ? `${config.listPath}/${existing.id}` : config.listPath)}
            disabled={saving}
          >
            {t("common:cancel")}
          </Button>
          <Button type="submit" loading={saving}>
            {t("common:save")}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}
