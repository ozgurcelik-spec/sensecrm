import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, SegmentedControl, Stack, Text, Textarea, TextInput, Input } from "@mantine/core";
import { TriangleAlert } from "lucide-react";
import { FormDialog } from "@/components/crm/form-dialog";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useCreateApiKey, useIntegrationsStatus, useUpdateApiKey } from "@/hooks/use-integrations";
import { blankToUndefined } from "@/lib/format";
import {
  API_KEY_DESCRIPTION_MAX,
  API_KEY_EXPIRY_PRESETS,
  API_KEY_MAX_CIDRS,
  API_KEY_NAME_MAX,
  apiKeyScopeOptions,
  apiKeyServerFieldErrors,
  cidrListProblem,
  endOfUtcDay,
  expiryFromDays,
  ownedScopes,
  parseCidrLines,
  utcDay,
  type ApiKeyFormField,
} from "@/lib/integrations";
import { useAuthStore } from "@/store/auth.store";
import type { ApiKey, ApiKeyCreated, ApiKeyInput } from "@/types";
import { ScopePicker } from "./scope-picker";

const DAY_MS = 86_400_000;
const FALLBACK_MAX_DAYS = 730;
const FALLBACK_DEFAULT_DAYS = 365;

type ExpiryChoice = `${number}` | "custom";

interface ApiKeyFormDialogProps {
  /** Key to edit (name, description and IP list only); undefined creates a new one. */
  apiKey?: ApiKey;
  onClose: () => void;
  /** Create only: the answer carries the raw key, which the caller shows exactly once. */
  onCreated?: (created: ApiKeyCreated) => void;
}

/**
 * Create / edit dialog of an API key. Creating: name, description, scopes (only `crm.*` and only
 * what the creator holds), expiry (30 / 90 / 365 days or a date, never beyond the server's maximum)
 * and an optional IP allow-list (CIDR lines). Editing changes name, description and the IP list only:
 * scope and expiry are fixed (create a new key instead). Server errors land on their fields.
 */
export function ApiKeyFormDialog({ apiKey, onClose, onCreated }: ApiKeyFormDialogProps) {
  const { t } = useTranslation(["integrations", "common"]);
  const me = useAuthStore((state) => state.me);
  const status = useIntegrationsStatus();
  const create = useCreateApiKey();
  const update = useUpdateApiKey();
  const isEdit = !!apiKey;

  const maxDays = status.data?.apiKeys.maxLifetimeDays ?? FALLBACK_MAX_DAYS;
  const defaultDays = status.data?.apiKeys.defaultLifetimeDays ?? FALLBACK_DEFAULT_DAYS;
  const presets = API_KEY_EXPIRY_PRESETS.filter((days) => days <= maxDays);
  const initialChoice: ExpiryChoice = presets.includes(defaultDays as (typeof API_KEY_EXPIRY_PRESETS)[number])
    ? (`${defaultDays}` as ExpiryChoice)
    : presets.length > 0
      ? (`${presets[presets.length - 1]}` as ExpiryChoice)
      : "custom";

  const options = useMemo(() => apiKeyScopeOptions(me), [me]);
  const owned = useMemo(() => ownedScopes(me), [me]);

  const [name, setName] = useState(apiKey?.name ?? "");
  const [description, setDescription] = useState(apiKey?.description ?? "");
  const [scopes, setScopes] = useState<string[]>([]);
  const [choice, setChoice] = useState<ExpiryChoice>(initialChoice);
  const [customDay, setCustomDay] = useState("");
  const [cidrText, setCidrText] = useState((apiKey?.allowedCidrs ?? []).join("\n"));
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<Partial<Record<ApiKeyFormField, string>>>({});

  const [now] = useState(() => Date.now());
  const today = utcDay(now);
  const latestDay = utcDay(now + maxDays * DAY_MS);
  const cidrs = parseCidrLines(cidrText);
  const cidrIssue = cidrListProblem(cidrs);

  const nameError = !name.trim()
    ? t("integrations:apiKeys.form.errors.nameRequired")
    : name.trim().length > API_KEY_NAME_MAX
      ? t("integrations:apiKeys.form.errors.nameMax", { max: API_KEY_NAME_MAX })
      : undefined;
  const descriptionError =
    description.length > API_KEY_DESCRIPTION_MAX
      ? t("integrations:apiKeys.form.errors.descriptionMax", { max: API_KEY_DESCRIPTION_MAX })
      : undefined;
  const scopeError = !isEdit && scopes.length === 0 ? t("integrations:apiKeys.form.errors.scopesRequired") : undefined;
  const expiryError =
    !isEdit && choice === "custom"
      ? !customDay
        ? t("integrations:apiKeys.form.errors.expiryRequired")
        : customDay <= today
          ? t("integrations:apiKeys.form.errors.expiryPast")
          : customDay > latestDay
            ? t("integrations:apiKeys.form.errors.expiryMax", { days: maxDays })
            : undefined
      : undefined;
  const cidrError = cidrIssue
    ? t(`integrations:apiKeys.form.cidrErrors.${cidrIssue.key}`, { value: cidrIssue.value, max: API_KEY_MAX_CIDRS })
    : undefined;
  const hasErrors = !!(nameError || descriptionError || scopeError || expiryError || cidrError);

  function edited(field: ApiKeyFormField) {
    setServerErrors((current) => ({ ...current, [field]: undefined }));
  }

  async function onSubmit() {
    setSubmitted(true);
    setServerErrors({});
    if (hasErrors) return;
    try {
      if (apiKey) {
        await update.mutateAsync({
          id: apiKey.id,
          patch: {
            name: name.trim(),
            description: blankToUndefined(description) ?? null,
            allowedCidrs: cidrs,
          },
        });
        toast({ variant: "success", description: t("integrations:apiKeys.updated") });
      } else {
        const input: ApiKeyInput = {
          name: name.trim(),
          scopes,
          expiresAt: choice === "custom" ? endOfUtcDay(customDay) : expiryFromDays(Number(choice)),
          ...(cidrs.length > 0 ? { allowedCidrs: cidrs } : {}),
          ...(blankToUndefined(description) ? { description: blankToUndefined(description) } : {}),
        };
        const created = await create.mutateAsync(input);
        onCreated?.(created);
        toast({ variant: "success", description: t("integrations:apiKeys.created") });
      }
      onClose();
    } catch (error) {
      const fields = apiKeyServerFieldErrors(error);
      if (Object.keys(fields).length > 0) setServerErrors(fields);
      else toastApiError(error);
    }
  }

  const shown = <F extends ApiKeyFormField>(field: F, local: string | undefined) =>
    serverErrors[field] ?? (submitted ? local : undefined);

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={isEdit ? t("integrations:apiKeys.editTitle") : t("integrations:apiKeys.createTitle")}
      onSubmit={(event) => {
        event.preventDefault();
        void onSubmit();
      }}
      loading={create.isPending || update.isPending}
      submitLabel={isEdit ? t("common:save") : t("common:create")}
      size="xl"
    >
      <TextInput
        label={t("integrations:apiKeys.form.name")}
        withAsterisk
        data-autofocus
        value={name}
        onChange={(event) => {
          setName(event.currentTarget.value);
          edited("name");
        }}
        error={shown("name", nameError)}
      />
      <Textarea
        label={t("integrations:apiKeys.form.description")}
        autosize
        minRows={2}
        maxRows={4}
        value={description}
        onChange={(event) => {
          setDescription(event.currentTarget.value);
          edited("description");
        }}
        error={shown("description", descriptionError)}
      />

      {!isEdit && (
        <>
          <ScopePicker
            options={options}
            owned={owned}
            value={scopes}
            onChange={(next) => {
              setScopes(next);
              edited("scopes");
            }}
            error={shown("scopes", scopeError)}
          />
          <Input.Wrapper
            label={t("integrations:apiKeys.form.expiry")}
            description={t("integrations:apiKeys.form.expiryHint", { days: maxDays })}
            withAsterisk
            error={shown("expiresAt", expiryError)}
          >
            <Stack gap="xs" mt="xs">
              <SegmentedControl
                aria-label={t("integrations:apiKeys.form.expiry")}
                value={choice}
                onChange={(value) => {
                  setChoice(value as ExpiryChoice);
                  edited("expiresAt");
                }}
                data={[
                  ...presets.map((days) => ({ value: `${days}`, label: t("integrations:apiKeys.form.days", { count: days }) })),
                  { value: "custom", label: t("integrations:apiKeys.form.customDate") },
                ]}
              />
              {choice === "custom" && (
                <TextInput
                  type="date"
                  aria-label={t("integrations:apiKeys.form.customDate")}
                  min={today}
                  max={latestDay}
                  value={customDay}
                  onChange={(event) => {
                    setCustomDay(event.currentTarget.value);
                    edited("expiresAt");
                  }}
                />
              )}
            </Stack>
          </Input.Wrapper>
        </>
      )}

      {isEdit && (
        <Text size="sm" c="dimmed" data-testid="scope-fixed">
          {t("integrations:apiKeys.form.scopeFixed")}
        </Text>
      )}

      <Textarea
        label={t("integrations:apiKeys.form.cidrs")}
        description={t("integrations:apiKeys.form.cidrsHint")}
        placeholder={"203.0.113.0/24\n2001:db8::/32"}
        autosize
        minRows={2}
        maxRows={6}
        styles={{ input: { fontFamily: "var(--mantine-font-family-monospace)" } }}
        value={cidrText}
        onChange={(event) => {
          setCidrText(event.currentTarget.value);
          edited("allowedCidrs");
        }}
        // A bad entry is reported while typing (no need to submit first).
        error={serverErrors.allowedCidrs ?? cidrError}
      />
      {cidrs.length === 0 && (
        <Alert color="yellow" variant="light" icon={<TriangleAlert size={16} />} data-testid="cidr-open-warning">
          {t("integrations:apiKeys.form.cidrsOpen")}
        </Alert>
      )}
    </FormDialog>
  );
}
