import { useMemo, useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Controller, useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import {
  Alert,
  Anchor,
  Button,
  Card,
  Group,
  Select,
  SimpleGrid,
  Stack,
  Text,
  Textarea,
  TextInput,
} from "@mantine/core";
import { ArrowLeft } from "lucide-react";
import { AccountPicker } from "@/components/crm/account-picker";
import { OwnerSelect } from "@/components/crm/owner-select";
import { PageHeader } from "@/components/page-header";
import { useContacts } from "@/hooks/use-contacts";
import { useDeals } from "@/hooks/use-deals";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { usePermission } from "@/hooks/use-permission";
import { useSaveOrder } from "@/hooks/use-orders";
import { useSaveQuote } from "@/hooks/use-quotes";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
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
import { blankToUndefined } from "@/lib/format";
import { formatYmd, ymdInZone } from "@/lib/zoned-time";
import { useAuthStore } from "@/store/auth.store";
import { CURRENCIES, PERMISSIONS, type OrderInput, type Quote, type QuoteInput, type SalesOrder } from "@/types";
import { LineItemsGrid } from "./line-items-grid";

export type DocumentKind = "quote" | "order";

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

const HEADER_FIELDS = [
  "subject",
  "accountId",
  "contactId",
  "dealId",
  "validUntil",
  "orderDate",
  "ownerUserId",
  "currency",
  "terms",
  "notes",
] as const;

const schema = z.object({
  subject: z
    .string()
    .trim()
    .min(1, "auth:validation.required")
    .max(200, "commerce:validation.subjectMax"),
  accountId: z.string().min(1, "auth:validation.required"),
  contactId: z.string(),
  dealId: z.string(),
  validUntil: z.string(),
  orderDate: z.string(),
  ownerUserId: z.string(),
  currency: z.string().min(1, "auth:validation.required"),
  terms: z.string().max(4000, "commerce:validation.termsMax"),
  notes: z.string().max(2000, "commerce:validation.notesMax"),
});

type FormValues = z.infer<typeof schema>;

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

interface DocumentEditorProps {
  kind: DocumentKind;
  /** The document being edited (must be a draft); omitted when creating. */
  existing?: Quote | SalesOrder;
  prefill?: EditorPrefill;
}

/**
 * Create / edit form of a quote or a sales order: header fields plus the shared line item grid with its
 * live total preview. Saves the contract body only (no computed amounts), maps server field errors
 * (`lines[i].field` onto grid cells) and shows the saved document's page afterwards.
 */
export function DocumentEditor({ kind, existing, prefill }: DocumentEditorProps) {
  const { t } = useTranslation(["commerce", "common", "auth", "crm"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const defaultOwnerId = useDefaultOwnerId();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const canReadDeals = usePermission(PERMISSIONS.crmDealsRead);
  const canReadProducts = usePermission(PERMISSIONS.crmProductsRead);
  const saveQuote = useSaveQuote();
  const saveOrder = useSaveOrder();
  const isQuote = kind === "quote";
  const section = isQuote ? "quotes" : "orders";
  const listPath = isQuote ? "/app/quotes" : "/app/orders";
  const saving = isQuote ? saveQuote.isPending : saveOrder.isPending;

  const existingQuote = isQuote ? (existing as Quote | undefined) : undefined;
  const existingOrder = isQuote ? undefined : (existing as SalesOrder | undefined);

  const [lines, setLines] = useState<LineDraft[]>(() =>
    existing ? linesFromServer(existing.lines) : [newLine()]
  );
  const [lineErrors, setLineErrors] = useState<LineErrors>({});
  const [formErrors, setFormErrors] = useState<string[]>([]);

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
      contactId: existing?.contactId ?? prefill?.contactId ?? "",
      dealId: existing?.dealId ?? prefill?.dealId ?? "",
      validUntil:
        existingQuote?.validUntil?.slice(0, 10) ??
        (isQuote && !existing ? todayPlus(DEFAULT_VALIDITY_DAYS, timeZone) : ""),
      orderDate: existingOrder?.orderDate?.slice(0, 10) ?? "",
      ownerUserId: existing?.ownerUserId ?? defaultOwnerId,
      currency: existing?.currency ?? prefill?.currency ?? "TRY",
      terms: existing?.terms ?? "",
      notes: existing?.notes ?? "",
    },
  });

  const accountId = useWatch({ control, name: "accountId" });
  const currency = useWatch({ control, name: "currency" });
  const contacts = useContacts(
    { page: 1, pageSize: 100, accountId: accountId || undefined },
    !!accountId && canReadContacts
  );
  const deals = useDeals(
    { page: 1, pageSize: 100, accountId: accountId || undefined },
    !!accountId && canReadDeals
  );

  const contactOptions = useMemo(() => {
    const list = (contacts.data?.items ?? []).map((c) => ({ value: c.id, label: c.fullName }));
    const current = existing?.contactId ?? prefill?.contactId;
    if (current && !list.some((o) => o.value === current)) {
      list.unshift({ value: current, label: existing?.contactName ?? prefill?.contactName ?? current });
    }
    return list;
  }, [contacts.data, existing, prefill]);

  const dealOptions = useMemo(() => {
    const list = (deals.data?.items ?? []).map((d) => ({ value: d.id, label: d.name }));
    const current = existing?.dealId ?? prefill?.dealId;
    if (current && !list.some((o) => o.value === current)) {
      list.unshift({ value: current, label: existing?.dealName ?? prefill?.dealName ?? current });
    }
    return list;
  }, [deals.data, existing, prefill]);

  const message = (key?: string) => (key ? t(key, { defaultValue: key }) : undefined);

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

  const onSubmit = handleSubmit(
    async (values) => {
      setFormErrors([]);
      if (!checkLines()) return;

      const common = {
        subject: values.subject.trim(),
        accountId: values.accountId,
        contactId: blankToUndefined(values.contactId),
        dealId: blankToUndefined(values.dealId),
        ownerUserId: blankToUndefined(values.ownerUserId),
        currency: values.currency.trim().toUpperCase(),
        terms: blankToUndefined(values.terms),
        notes: blankToUndefined(values.notes),
        lines: toLineInputs(lines),
      };

      try {
        let id: string;
        if (isQuote) {
          const body: QuoteInput = { ...common, validUntil: blankToUndefined(values.validUntil) };
          id = await saveQuote.mutateAsync({ id: existing?.id, ...body });
        } else {
          const body: OrderInput = { ...common, orderDate: blankToUndefined(values.orderDate) };
          id = await saveOrder.mutateAsync({ id: existing?.id, ...body });
        }
        toast({
          variant: "success",
          description: t(`commerce:${section}.${existing ? "updated" : "created"}`),
        });
        navigate(`${listPath}/${id}`);
      } catch (error) {
        handleSaveError(error);
      }
    },
    () => {
      checkLines();
    }
  );

  function handleSaveError(error: unknown) {
    const problem = getApiProblem(error);
    if (!problem?.errors) {
      // commerce.related_not_found, *.not_editable, commerce.concurrent_update, ... : translated by code.
      toastApiError(error);
      return;
    }
    const { lineErrors: fromServer, rest } = splitLineErrors(problem.errors);
    setLineErrors(fromServer);
    const unmatched: string[] = [];
    for (const [rawField, messages] of Object.entries(rest)) {
      const field = rawField
        .split(".")
        .map((segment) => segment.charAt(0).toLowerCase() + segment.slice(1))
        .join(".");
      const target = HEADER_FIELDS.find((f) => f === field);
      if (target && messages[0]) setError(target, { type: "server", message: messages[0] });
      else unmatched.push(...messages);
    }
    setFormErrors(unmatched);
    // Errors that landed on a field or a cell are visible there; anything else needs a message.
    if (unmatched.length === 0 && !hasLineErrors(fromServer) && Object.keys(rest).length === 0) {
      toastApiError(error);
    }
  }

  const title = t(`commerce:${section}.${existing ? "editTitle" : "newTitle"}`, {
    number: existing?.number,
  });

  return (
    <form onSubmit={onSubmit} noValidate>
      <Stack gap="md">
        <Anchor
          component={Link}
          to={existing ? `${listPath}/${existing.id}` : listPath}
          size="sm"
        >
          <Group gap={4}>
            <ArrowLeft size={14} />
            {existing ? existing.number : t(`commerce:${section}.title`)}
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
              <Controller
                control={control}
                name="accountId"
                render={({ field }) =>
                  canReadAccounts ? (
                    <AccountPicker
                      label={t("commerce:fields.account")}
                      value={field.value || null}
                      onChange={(value) => {
                        field.onChange(value ?? "");
                        // The contact and the deal belong to the previous account.
                        setValue("contactId", "");
                        setValue("dealId", "");
                      }}
                      selectedName={existing?.accountName ?? prefill?.accountName}
                      withAsterisk
                      error={message(errors.accountId?.message)}
                    />
                  ) : (
                    <TextInput
                      label={t("commerce:fields.account")}
                      value={existing?.accountName ?? prefill?.accountName ?? field.value}
                      disabled
                      withAsterisk
                      error={message(errors.accountId?.message)}
                      readOnly
                    />
                  )
                }
              />
              {canReadContacts && (
                <Controller
                  control={control}
                  name="contactId"
                  render={({ field }) => (
                    <Select
                      label={t("commerce:fields.contact")}
                      placeholder={accountId ? undefined : t("commerce:editor.needsAccount")}
                      data={contactOptions}
                      value={field.value || null}
                      onChange={(value) => field.onChange(value ?? "")}
                      disabled={!accountId}
                      searchable
                      clearable
                      nothingFoundMessage={t("crm:noOptions")}
                      error={message(errors.contactId?.message)}
                    />
                  )}
                />
              )}
              {canReadDeals && (
                <Controller
                  control={control}
                  name="dealId"
                  render={({ field }) => (
                    <Select
                      label={t("commerce:fields.deal")}
                      placeholder={accountId ? undefined : t("commerce:editor.needsAccount")}
                      data={dealOptions}
                      value={field.value || null}
                      onChange={(value) => field.onChange(value ?? "")}
                      disabled={!accountId}
                      searchable
                      clearable
                      nothingFoundMessage={t("crm:noOptions")}
                      error={message(errors.dealId?.message)}
                    />
                  )}
                />
              )}
              {isQuote ? (
                <TextInput
                  label={t("commerce:fields.validUntil")}
                  type="date"
                  error={message(errors.validUntil?.message)}
                  {...register("validUntil")}
                />
              ) : (
                <TextInput
                  label={t("commerce:fields.orderDate")}
                  type="date"
                  description={t("commerce:editor.orderDateHint")}
                  error={message(errors.orderDate?.message)}
                  {...register("orderDate")}
                />
              )}
              <Controller
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
              <Controller
                control={control}
                name="currency"
                render={({ field }) => (
                  <Select
                    label={t("commerce:fields.currency")}
                    data={[...CURRENCIES]}
                    value={field.value}
                    onChange={(value) => field.onChange(value ?? "TRY")}
                    allowDeselect={false}
                    error={message(errors.currency?.message)}
                  />
                )}
              />
            </SimpleGrid>
          </Stack>
        </Card>

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
            onClick={() => navigate(existing ? `${listPath}/${existing.id}` : listPath)}
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
