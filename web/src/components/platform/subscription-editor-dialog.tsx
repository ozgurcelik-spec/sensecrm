import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
  Alert,
  Button,
  Divider,
  Group,
  Modal,
  NumberInput,
  Select,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
} from "@mantine/core";
import { useUpdatePlatformSubscription } from "@/hooks/use-platform";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiErrorMessage, getApiProblem } from "@/lib/api-error";
import {
  isInvalidLimit,
  serverFieldErrors,
  toOverridesBody,
  toOverridesDraft,
  type LimitDraft,
  type LimitMode,
  type ModuleMode,
  type OverridesDraft,
} from "@/lib/platform";
import {
  GATED_MODULES,
  RECORD_MODULES,
  type GatedModule,
  type PlatformOrganizationDetail,
  type PlatformPlan,
  type PlatformSubscriptionResult,
} from "@/types";

interface SubscriptionEditorDialogProps {
  org: PlatformOrganizationDetail;
  plans: PlatformPlan[];
  onClose: () => void;
  onSaved: (result: PlatformSubscriptionResult) => void;
}

interface LimitFieldProps {
  label: string;
  draft: LimitDraft;
  planHint?: string;
  error?: string;
  onChange: (draft: LimitDraft) => void;
}

/** One limit: follow the plan, unlimited, or a custom number. */
function LimitField({ label, draft, planHint, error, onChange }: LimitFieldProps) {
  const { t } = useTranslation(["platform"]);
  return (
    <Stack gap={4}>
      <Group gap="xs" align="flex-start" wrap="nowrap">
        <Select
          label={label}
          aria-label={label}
          w={190}
          allowDeselect={false}
          data={[
            { value: "plan", label: t("platform:subscription.modePlan") },
            { value: "unlimited", label: t("platform:subscription.modeUnlimited") },
            { value: "custom", label: t("platform:subscription.modeCustom") },
          ]}
          value={draft.mode}
          onChange={(mode) => onChange({ ...draft, mode: (mode ?? "plan") as LimitMode })}
        />
        {draft.mode === "custom" && (
          <NumberInput
            label={t("platform:subscription.limitValue")}
            aria-label={t("platform:subscription.limitValueOf", { limit: label })}
            w={110}
            min={0}
            allowDecimal={false}
            allowNegative={false}
            value={draft.value}
            onChange={(value) =>
              onChange({ ...draft, value: typeof value === "number" ? value : "" })
            }
            error={isInvalidLimit(draft) ? t("platform:subscription.limitInvalid") : undefined}
          />
        )}
      </Group>
      {planHint && draft.mode === "plan" && (
        <Text size="xs" c="dimmed">
          {planHint}
        </Text>
      )}
      {error && (
        <Text size="xs" c="red" role="alert">
          {error}
        </Text>
      )}
    </Stack>
  );
}

/**
 * Plan / trial / overrides editor (`PUT /platform/organizations/{id}/subscription`, a full and
 * permanent replacement). Server validation errors land on their fields, a missing plan on the plan
 * field, anything else in a toast. The over-limit report is handed to the page after a save.
 */
export function SubscriptionEditorDialog({
  org,
  plans,
  onClose,
  onSaved,
}: SubscriptionEditorDialogProps) {
  const { t } = useTranslation(["platform", "subscription", "common"]);
  const update = useUpdatePlatformSubscription(org.tenantId);
  const [planCode, setPlanCode] = useState(org.planCode);
  const [trialEndsOn, setTrialEndsOn] = useState(org.trialEndsOn ?? "");
  const [draft, setDraft] = useState<OverridesDraft>(() => toOverridesDraft(org.overrides));
  const [errors, setErrors] = useState<Record<string, string>>({});

  const selectable = plans.filter((p) => p.isActive || p.code === org.planCode);
  const plan = plans.find((p) => p.code === planCode);
  const planLimitText = (value: number | null | undefined): string | undefined => {
    if (!plan) return undefined;
    return t("platform:subscription.planValue", {
      value:
        value === null || value === undefined
          ? t("platform:subscription.unlimited")
          : String(value),
    });
  };

  const hasInvalidLimit =
    isInvalidLimit(draft.maxUsers) || Object.values(draft.maxRecords).some(isInvalidLimit);

  function setLimit(key: "maxUsers" | string, next: LimitDraft) {
    setDraft((current) =>
      key === "maxUsers"
        ? { ...current, maxUsers: next }
        : { ...current, maxRecords: { ...current.maxRecords, [key]: next } }
    );
  }

  function setModule(module: GatedModule, mode: ModuleMode) {
    setDraft((current) => ({ ...current, modules: { ...current.modules, [module]: mode } }));
  }

  async function save() {
    setErrors({});
    try {
      const result = await update.mutateAsync({
        planCode,
        trialEndsOn: trialEndsOn || undefined,
        overrides: toOverridesBody(draft),
      });
      toast({ variant: "success", description: t("platform:subscription.saved") });
      onSaved(result);
    } catch (error) {
      const fields = serverFieldErrors(error);
      if (getApiProblem(error)?.code === "platform.plan_not_found") {
        setErrors({ planCode: getApiErrorMessage(error) });
      } else if (Object.keys(fields).length > 0) {
        setErrors(fields);
      } else {
        toastApiError(error);
      }
    }
  }

  const moduleLabel = (module: string) =>
    t(`subscription:modules.${module}`, { defaultValue: module });

  return (
    <Modal opened onClose={onClose} title={t("platform:subscription.title")} centered size="lg">
      <Stack gap="md">
        <Select
          label={t("platform:subscription.plan")}
          withAsterisk
          allowDeselect={false}
          data={selectable.map((p) => ({
            value: p.code,
            label: p.isActive ? p.name : `${p.name} (${t("platform:plans.inactive")})`,
          }))}
          value={planCode}
          onChange={(value) => value && setPlanCode(value)}
          error={errors.planCode}
        />
        <Group align="flex-end" gap="xs" wrap="nowrap">
          <TextInput
            type="date"
            label={t("platform:subscription.trialEndsOn")}
            description={t("platform:subscription.trialHint")}
            value={trialEndsOn}
            onChange={(event) => setTrialEndsOn(event.currentTarget.value)}
            error={errors.trialEndsOn}
            flex={1}
          />
          <Button
            variant="default"
            onClick={() => setTrialEndsOn("")}
            disabled={trialEndsOn === ""}
          >
            {t("platform:subscription.removeTrial")}
          </Button>
        </Group>

        <Divider label={t("platform:subscription.overrides")} labelPosition="left" />
        <Text size="xs" c="dimmed">
          {t("platform:subscription.overridesHint")}
        </Text>
        {errors.overrides && (
          <Alert color="red" variant="light" role="alert">
            {errors.overrides}
          </Alert>
        )}

        <LimitField
          label={t("platform:subscription.maxUsers")}
          draft={draft.maxUsers}
          planHint={planLimitText(plan?.limits.maxUsers)}
          error={errors["overrides.maxUsers"]}
          onChange={(next) => setLimit("maxUsers", next)}
        />
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
          {RECORD_MODULES.map((module) => (
            <LimitField
              key={module}
              label={t("platform:subscription.maxRecordsOf", { module: moduleLabel(module) })}
              draft={draft.maxRecords[module] ?? { mode: "plan", value: "" }}
              planHint={planLimitText(plan?.limits.maxRecords[module])}
              error={errors[`overrides.maxRecords.${module}`]}
              onChange={(next) => setLimit(module, next)}
            />
          ))}
        </SimpleGrid>

        <Divider label={t("platform:subscription.modules")} labelPosition="left" />
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
          {GATED_MODULES.map((module) => (
            <Stack key={module} gap={4}>
              <Select
                label={moduleLabel(module)}
                aria-label={t("platform:subscription.moduleOf", { module: moduleLabel(module) })}
                allowDeselect={false}
                data={[
                  { value: "plan", label: t("platform:subscription.modulePlan") },
                  { value: "on", label: t("platform:subscription.moduleOn") },
                  { value: "off", label: t("platform:subscription.moduleOff") },
                ]}
                value={draft.modules[module]}
                onChange={(mode) => setModule(module, (mode ?? "plan") as ModuleMode)}
              />
              {errors[`overrides.modules.${module}`] && (
                <Text size="xs" c="red" role="alert">
                  {errors[`overrides.modules.${module}`]}
                </Text>
              )}
            </Stack>
          ))}
        </SimpleGrid>

        <Group justify="flex-end" mt="sm">
          <Button variant="default" onClick={onClose} disabled={update.isPending}>
            {t("common:cancel")}
          </Button>
          <Button
            onClick={() => void save()}
            loading={update.isPending}
            disabled={hasInvalidLimit || !planCode}
          >
            {t("common:save")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
