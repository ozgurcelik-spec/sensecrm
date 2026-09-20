import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Switch, Textarea, TextInput } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useCreateWebhook, useUpdateWebhook } from "@/hooks/use-integrations";
import { blankToUndefined } from "@/lib/format";
import {
  WEBHOOK_DESCRIPTION_MAX,
  WEBHOOK_NAME_MAX,
  validateWebhookUrl,
  webhookServerFieldErrors,
  webhookUrlErrorText,
  type WebhookFormField,
} from "@/lib/integrations";
import type { WebhookInput, WebhookSubscription, WebhookSubscriptionCreated } from "@/types";
import { EventTypePicker } from "./event-type-picker";

const URL_PREFIX = "url:";

const schema = z
  .object({
    name: z
      .string()
      .trim()
      .min(1, "integrations:webhooks.form.errors.nameRequired")
      .max(WEBHOOK_NAME_MAX, "integrations:webhooks.form.errors.nameMax"),
    url: z.string(),
    eventTypes: z.array(z.string()).min(1, "integrations:webhooks.form.errors.eventTypesRequired"),
    description: z.string().max(WEBHOOK_DESCRIPTION_MAX, "integrations:webhooks.form.errors.descriptionMax"),
    enabled: z.boolean(),
  })
  // The URL rules are the syntactic SSRF rules of the plan (the server checks them again).
  .superRefine((values, ctx) => {
    const reason = validateWebhookUrl(values.url);
    if (reason) ctx.addIssue({ code: "custom", path: ["url"], message: `${URL_PREFIX}${reason}` });
  });

type FormValues = z.infer<typeof schema>;

const FIELDS: readonly WebhookFormField[] = ["name", "url", "eventTypes", "description"];

interface WebhookFormDialogProps {
  /** Subscription to edit; undefined creates a new one. */
  webhook?: WebhookSubscription;
  onClose: () => void;
  /** Create only: the answer carries the raw secret, which the caller shows exactly once. */
  onCreated?: (created: WebhookSubscriptionCreated) => void;
}

/**
 * Create / edit dialog of a webhook subscription: name, HTTPS URL (client pre-validation, server
 * `webhook.url_invalid` reasons and `webhook.name_taken` land on their fields), event types from
 * the catalog, description and the enabled switch. Editing is a full replacement (`PUT`).
 */
export function WebhookFormDialog({ webhook, onClose, onCreated }: WebhookFormDialogProps) {
  const { t } = useTranslation(["integrations", "common"]);
  const create = useCreateWebhook();
  const update = useUpdateWebhook();
  const isEdit = !!webhook;

  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: webhook?.name ?? "",
      url: webhook?.url ?? "",
      eventTypes: webhook?.eventTypes ?? [],
      description: webhook?.description ?? "",
      enabled: webhook?.enabled ?? true,
    },
  });

  const message = (error: { message?: string; type?: string } | undefined): string | undefined => {
    const text = error?.message;
    if (!text) return undefined;
    if (error.type === "server") return text;
    return text.startsWith(URL_PREFIX) ? webhookUrlErrorText(text.slice(URL_PREFIX.length)) : t(text);
  };

  const onSubmit = handleSubmit(async (values) => {
    const input: WebhookInput = {
      name: values.name.trim(),
      url: values.url.trim(),
      eventTypes: values.eventTypes,
      description: blankToUndefined(values.description),
      enabled: values.enabled,
    };
    try {
      if (webhook) {
        await update.mutateAsync({ id: webhook.id, input });
        toast({ variant: "success", description: t("integrations:webhooks.updated") });
      } else {
        const created = await create.mutateAsync(input);
        onCreated?.(created);
        toast({ variant: "success", description: t("integrations:webhooks.created") });
      }
      onClose();
    } catch (error) {
      const fields = webhookServerFieldErrors(error);
      const matched = FIELDS.filter((field) => fields[field]);
      if (matched.length === 0) {
        toastApiError(error);
        return;
      }
      for (const field of matched) setError(field, { type: "server", message: fields[field] });
    }
  });

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={isEdit ? t("integrations:webhooks.editTitle") : t("integrations:webhooks.createTitle")}
      onSubmit={onSubmit}
      loading={create.isPending || update.isPending}
      submitLabel={isEdit ? t("common:save") : t("common:create")}
      size="lg"
    >
      <TextInput
        label={t("integrations:webhooks.form.name")}
        withAsterisk
        data-autofocus
        error={message(errors.name)}
        {...register("name")}
      />
      <TextInput
        label={t("integrations:webhooks.form.url")}
        description={t("integrations:webhooks.form.urlHint")}
        placeholder="https://example.com/webhooks/crm"
        withAsterisk
        autoComplete="off"
        inputMode="url"
        error={message(errors.url)}
        {...register("url")}
      />
      <Controller
        control={control}
        name="eventTypes"
        render={({ field }) => (
          <EventTypePicker value={field.value} onChange={field.onChange} error={message(errors.eventTypes)} />
        )}
      />
      <Textarea
        label={t("integrations:webhooks.form.description")}
        autosize
        minRows={2}
        maxRows={5}
        error={message(errors.description)}
        {...register("description")}
      />
      <Controller
        control={control}
        name="enabled"
        render={({ field }) => (
          <Switch
            label={t("integrations:webhooks.form.enabled")}
            description={t("integrations:webhooks.form.enabledHint")}
            checked={field.value}
            onChange={(event) => field.onChange(event.currentTarget.checked)}
          />
        )}
      />
    </FormDialog>
  );
}
