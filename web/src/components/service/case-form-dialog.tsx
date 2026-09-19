import { useState } from "react";
import { useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Controller, useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Select, SimpleGrid, TextInput, Textarea } from "@mantine/core";
import { AccountPicker } from "@/components/crm/account-picker";
import { FormDialog } from "@/components/crm/form-dialog";
import { useSaveCase } from "@/hooks/use-cases";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors, getApiProblem } from "@/lib/api-error";
import { blankToUndefined } from "@/lib/format";
import {
  CASE_CHANNELS,
  CASE_PRIORITIES,
  PERMISSIONS,
  type CaseChannel,
  type CaseCreateInput,
  type CaseDetail,
  type CasePriority,
  type CaseUpdateInput,
} from "@/types";
import { AssigneeSelect } from "./assignee-select";
import { ContactPicker } from "./contact-picker";

const schema = z.object({
  subject: z.string().trim().min(1, "auth:validation.required").max(200, "service:validation.subjectMax"),
  description: z.string().max(8000, "service:validation.descriptionMax"),
  accountId: z.string(),
  contactId: z.string(),
  priority: z.enum(CASE_PRIORITIES),
  channel: z.enum(CASE_CHANNELS),
  assignedUserId: z.string(),
});

type FormValues = z.infer<typeof schema>;

const FIELDS = [
  "subject",
  "description",
  "accountId",
  "contactId",
  "priority",
  "channel",
  "assignedUserId",
] as const;

/** Server error codes that belong to one field of the form. */
const CODE_FIELDS: Record<string, (typeof FIELDS)[number]> = {
  "case.contact_account_mismatch": "contactId",
  "case.contact_not_found": "contactId",
  "case.account_not_found": "accountId",
  "owner.not_member": "assignedUserId",
};

export interface FixedAccount {
  id: string;
  name: string;
}

export interface FixedContact {
  id: string;
  name: string;
  /** The contact's account (locked together with the contact when there is one). */
  accountId?: string;
  accountName?: string;
}

interface CaseFormDialogProps {
  /** Edit an existing case (subject, description, account, contact and channel only). */
  item?: CaseDetail;
  /** "Open a case" from an account page: the account is preset and locked. */
  fixedAccount?: FixedAccount;
  /** "Open a case" from a contact page: the contact is preset and locked. */
  fixedContact?: FixedContact;
  onClose: () => void;
  onSaved?: (id: string) => void;
}

/**
 * Create / edit dialog of a case. Picking a contact fills a still empty account; picking an account
 * narrows the contacts to that account. Server field errors and `case.contact_account_mismatch` /
 * `case.*_not_found` / `owner.not_member` are put on their fields. A new case opens its detail page.
 */
export function CaseFormDialog({
  item,
  fixedAccount,
  fixedContact,
  onClose,
  onSaved,
}: CaseFormDialogProps) {
  const { t } = useTranslation(["service", "common", "auth"]);
  const navigate = useNavigate();
  const save = useSaveCase();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const isEdit = !!item;

  const lockedAccount = fixedAccount ?? (fixedContact?.accountId
    ? { id: fixedContact.accountId, name: fixedContact.accountName ?? "" }
    : undefined);
  const [accountName, setAccountName] = useState<string | undefined>(
    item?.accountName ?? lockedAccount?.name
  );
  const [contactName, setContactName] = useState<string | undefined>(
    item?.contactName ?? fixedContact?.name
  );
  // Account of the selected contact when it is known (only then a different account clears the contact).
  const [contactAccountId, setContactAccountId] = useState<string | undefined>(
    fixedContact?.accountId ?? (item?.contactId ? item.accountId : undefined)
  );

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
      subject: item?.subject ?? "",
      description: item?.description ?? "",
      accountId: item?.accountId ?? lockedAccount?.id ?? "",
      contactId: item?.contactId ?? fixedContact?.id ?? "",
      priority: item?.priority ?? "normal",
      channel: item?.channel ?? "other",
      assignedUserId: item?.assignedUserId ?? "",
    },
  });

  const accountId = useWatch({ control, name: "accountId" });
  const message = (key?: string) => key && t(key, { defaultValue: key });

  const onSubmit = handleSubmit(async (values) => {
    const common = {
      subject: values.subject.trim(),
      description: blankToUndefined(values.description),
      accountId: blankToUndefined(values.accountId),
      contactId: blankToUndefined(values.contactId),
      channel: values.channel as CaseChannel,
    };
    const payload: CaseCreateInput | CaseUpdateInput = isEdit
      ? common
      : {
          ...common,
          priority: values.priority as CasePriority,
          assignedUserId: blankToUndefined(values.assignedUserId),
        };
    try {
      const id = await save.mutateAsync({ id: item?.id, ...payload });
      toast({
        variant: "success",
        description: isEdit ? t("service:updated") : t("service:created"),
      });
      onSaved?.(id);
      if (!isEdit) void navigate(`/app/cases/${id}`);
      onClose();
    } catch (error) {
      const matched = applyValidationErrors(error, setError, FIELDS);
      const code = getApiProblem(error)?.code;
      const codeField = code ? CODE_FIELDS[code] : undefined;
      if (code && codeField) {
        setError(codeField, { type: "server", message: t(`common:errors.${code}`) });
      } else if (!matched) {
        toastApiError(error);
      }
    }
  });

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={isEdit ? t("service:editTitle") : t("service:createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={isEdit ? t("common:save") : t("common:create")}
    >
      <TextInput
        label={t("service:fields.subject")}
        withAsterisk
        data-autofocus
        error={message(errors.subject?.message)}
        {...register("subject")}
      />
      <Textarea
        label={t("service:fields.description")}
        autosize
        minRows={3}
        maxRows={8}
        error={message(errors.description?.message)}
        {...register("description")}
      />

      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        {lockedAccount ? (
          <TextInput
            label={t("service:fields.account")}
            value={lockedAccount.name}
            readOnly
            disabled
          />
        ) : (
          canReadAccounts && (
            <Controller
              control={control}
              name="accountId"
              render={({ field }) => (
                <AccountPicker
                  value={field.value || null}
                  onChange={(value, option) => {
                    field.onChange(value ?? "");
                    setAccountName(option?.label);
                    // A contact of another account no longer fits the chosen account.
                    if (contactAccountId && (value ?? "") !== contactAccountId) {
                      setValue("contactId", "");
                      setContactName(undefined);
                      setContactAccountId(undefined);
                    }
                  }}
                  selectedName={accountName}
                  clearable
                  error={message(errors.accountId?.message)}
                />
              )}
            />
          )
        )}

        {fixedContact ? (
          <TextInput
            label={t("service:fields.contact")}
            value={fixedContact.name}
            readOnly
            disabled
          />
        ) : (
          canReadContacts && (
            <Controller
              control={control}
              name="contactId"
              render={({ field }) => (
                <ContactPicker
                  value={field.value || null}
                  accountId={accountId || undefined}
                  selectedName={contactName}
                  clearable
                  onChange={(contact) => {
                    field.onChange(contact.id ?? "");
                    setContactName(contact.name);
                    setContactAccountId(contact.accountId);
                    // Picking a contact fills a still empty account from the contact's own account.
                    if (contact.id && !accountId && contact.accountId) {
                      setValue("accountId", contact.accountId);
                      setAccountName(contact.accountName);
                    }
                  }}
                  error={message(errors.contactId?.message)}
                />
              )}
            />
          )
        )}
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, sm: isEdit ? 1 : 2 }} spacing="sm">
        {!isEdit && (
          <Controller
            control={control}
            name="priority"
            render={({ field }) => (
              <Select
                label={t("service:fields.priority")}
                data={CASE_PRIORITIES.map((v) => ({ value: v, label: t(`service:priorities.${v}`) }))}
                value={field.value}
                onChange={(value) => field.onChange(value ?? "normal")}
                allowDeselect={false}
                error={message(errors.priority?.message)}
              />
            )}
          />
        )}
        <Controller
          control={control}
          name="channel"
          render={({ field }) => (
            <Select
              label={t("service:fields.channel")}
              data={CASE_CHANNELS.map((v) => ({ value: v, label: t(`service:channels.${v}`) }))}
              value={field.value}
              onChange={(value) => field.onChange(value ?? "other")}
              allowDeselect={false}
              error={message(errors.channel?.message)}
            />
          )}
        />
      </SimpleGrid>

      {!isEdit && (
        <Controller
          control={control}
          name="assignedUserId"
          render={({ field }) => (
            <AssigneeSelect
              label={t("service:fields.assignee")}
              value={field.value || null}
              onChange={(userId) => field.onChange(userId ?? "")}
              error={message(errors.assignedUserId?.message)}
            />
          )}
        />
      )}
    </FormDialog>
  );
}
