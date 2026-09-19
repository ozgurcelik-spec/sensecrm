import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Select, SimpleGrid, TextInput } from "@mantine/core";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { useSaveLead } from "@/hooks/use-leads";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import { blankToUndefined } from "@/lib/format";
import {
  LEAD_RATINGS,
  LEAD_SOURCES,
  LEAD_STATUSES,
  type Lead,
  type LeadRating,
  type LeadSource,
  type LeadStatus,
} from "@/types";
import { FormDialog } from "./form-dialog";
import { OwnerSelect } from "./owner-select";

const schema = z.object({
  firstName: z.string(),
  lastName: z.string().trim().min(1, "auth:validation.required"),
  company: z.string().trim().min(1, "auth:validation.required"),
  email: z
    .string()
    .trim()
    .refine((v) => v === "" || z.email().safeParse(v).success, "auth:validation.email"),
  phone: z.string(),
  source: z.enum(LEAD_SOURCES),
  status: z.enum(LEAD_STATUSES),
  rating: z.string(),
  ownerUserId: z.string(),
});

type FormValues = z.infer<typeof schema>;

const FIELDS = [
  "firstName",
  "lastName",
  "company",
  "email",
  "phone",
  "source",
  "status",
  "rating",
  "ownerUserId",
] as const;

interface LeadFormDialogProps {
  lead?: Lead;
  onClose: () => void;
  onSaved?: (id: string) => void;
}

export function LeadFormDialog({ lead, onClose, onSaved }: LeadFormDialogProps) {
  const { t } = useTranslation(["crm", "common", "auth"]);
  const save = useSaveLead();
  const defaultOwnerId = useDefaultOwnerId();
  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      firstName: lead?.firstName ?? "",
      lastName: lead?.lastName ?? "",
      company: lead?.company ?? "",
      email: lead?.email ?? "",
      phone: lead?.phone ?? "",
      source: lead?.source ?? "other",
      status: lead?.status ?? "new",
      rating: lead?.rating ?? "",
      ownerUserId: lead?.ownerUserId ?? defaultOwnerId,
    },
  });

  const message = (key?: string) => key && t(key, { defaultValue: key });

  const onSubmit = handleSubmit(async (values) => {
    try {
      const id = await save.mutateAsync({
        id: lead?.id,
        firstName: blankToUndefined(values.firstName),
        lastName: values.lastName.trim(),
        company: values.company.trim(),
        email: blankToUndefined(values.email),
        phone: blankToUndefined(values.phone),
        source: values.source as LeadSource,
        // A new lead always starts as "new" server-side; status is only sent when editing.
        status: lead ? (values.status as LeadStatus) : undefined,
        rating: (blankToUndefined(values.rating) as LeadRating | undefined) ?? undefined,
        ownerUserId: blankToUndefined(values.ownerUserId),
      });
      toast({
        variant: "success",
        description: lead ? t("crm:leads.updated") : t("crm:leads.created"),
      });
      onSaved?.(id);
      onClose();
    } catch (error) {
      if (!applyValidationErrors(error, setError, FIELDS)) toastApiError(error);
    }
  });

  const editableStatuses = LEAD_STATUSES.filter((s) => s !== "converted");

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={lead ? t("crm:leads.editTitle") : t("crm:leads.createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={lead ? t("common:save") : t("common:create")}
    >
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <TextInput
          label={t("crm:leads.fields.firstName")}
          data-autofocus
          error={message(errors.firstName?.message)}
          {...register("firstName")}
        />
        <TextInput
          label={t("crm:leads.fields.lastName")}
          withAsterisk
          error={message(errors.lastName?.message)}
          {...register("lastName")}
        />
        <TextInput
          label={t("crm:leads.fields.company")}
          withAsterisk
          error={message(errors.company?.message)}
          {...register("company")}
        />
        <TextInput
          label={t("crm:leads.fields.email")}
          type="email"
          error={message(errors.email?.message)}
          {...register("email")}
        />
        <TextInput
          label={t("crm:leads.fields.phone")}
          error={message(errors.phone?.message)}
          {...register("phone")}
        />
        <Controller
          control={control}
          name="source"
          render={({ field }) => (
            <Select
              label={t("crm:leads.fields.source")}
              data={LEAD_SOURCES.map((s) => ({ value: s, label: t(`crm:leads.sources.${s}`) }))}
              value={field.value}
              onChange={(value) => field.onChange(value ?? "other")}
              allowDeselect={false}
              error={message(errors.source?.message)}
            />
          )}
        />
        {lead && (
          <Controller
            control={control}
            name="status"
            render={({ field }) => (
              <Select
                label={t("crm:leads.fields.status")}
                data={editableStatuses.map((s) => ({
                  value: s,
                  label: t(`crm:leads.statuses.${s}`),
                }))}
                value={field.value}
                onChange={(value) => field.onChange(value ?? "new")}
                allowDeselect={false}
                error={message(errors.status?.message)}
              />
            )}
          />
        )}
        <Controller
          control={control}
          name="rating"
          render={({ field }) => (
            <Select
              label={t("crm:leads.fields.rating")}
              data={LEAD_RATINGS.map((r) => ({ value: r, label: t(`crm:leads.ratings.${r}`) }))}
              value={field.value || null}
              onChange={(value) => field.onChange(value ?? "")}
              clearable
              error={message(errors.rating?.message)}
            />
          )}
        />
      </SimpleGrid>
      <Controller
        control={control}
        name="ownerUserId"
        render={({ field }) => (
          <OwnerSelect
            value={field.value || null}
            onChange={(value) => field.onChange(value ?? "")}
            currentOwnerId={lead?.ownerUserId}
            currentOwnerName={lead?.ownerName}
            error={message(errors.ownerUserId?.message)}
          />
        )}
      />
    </FormDialog>
  );
}
