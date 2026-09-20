import { useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Anchor, Badge, Button, Card, Group, Stack, Switch, Text, TextInput } from "@mantine/core";
import { Info } from "lucide-react";
import { useUpdateNotificationSettings } from "@/hooks/use-notifications";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import { formatNumber } from "@/lib/format";
import {
  REPLY_TO_MAX,
  SENDER_NAME_MAX,
  validateSettings,
  type SettingsField as Field,
} from "@/lib/notification-settings";
import { useAuthStore } from "@/store/auth.store";
import type {
  NotificationChannelState,
  NotificationSettingsInput,
  NotificationTenantSettings,
} from "@/types";
import { TestEmailButton } from "./test-email-button";

interface ChannelRowProps {
  channel: "email" | "sms";
  state: NotificationChannelState;
  checked: boolean;
  onChange: (value: boolean) => void;
  canWrite: boolean;
}

function ChannelRow({ channel, state, checked, onChange, canWrite }: ChannelRowProps) {
  const { t } = useTranslation(["notifications"]);
  const unavailable = !state.availableByPlatform || !state.availableByPlan;
  return (
    <Stack gap={4}>
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <div>
          <Text fw={500}>{t(`notifications:settings.${channel}.label`)}</Text>
          <Text size="sm" c="dimmed">
            {t(`notifications:settings.${channel}.hint`)}
          </Text>
        </div>
        <Group gap="sm" wrap="nowrap">
          <Badge variant="light" color={state.effective ? "green" : "gray"} data-testid={`effective-${channel}`}>
            {state.effective ? t("notifications:settings.effective") : t("notifications:settings.notEffective")}
          </Badge>
          <Switch
            aria-label={t(`notifications:settings.${channel}.label`)}
            checked={checked}
            disabled={!canWrite || unavailable}
            onChange={(event) => onChange(event.currentTarget.checked)}
          />
        </Group>
      </Group>
      {!state.availableByPlatform && (
        <Alert color="yellow" variant="light" icon={<Info size={16} />} data-testid={`reason-platform-${channel}`}>
          {t("notifications:settings.reason.platform")}
        </Alert>
      )}
      {state.availableByPlatform && !state.availableByPlan && (
        <Alert color="yellow" variant="light" icon={<Info size={16} />} data-testid={`reason-plan-${channel}`}>
          {t("notifications:settings.reason.plan")}{" "}
          <Anchor component={Link} to="/app/settings/plan" size="sm">
            {t("notifications:settings.planLink")}
          </Anchor>
        </Alert>
      )}
    </Stack>
  );
}

interface TenantSettingsFormProps {
  settings: NotificationTenantSettings;
  /** Manage permission and full access; otherwise the form is read-only. */
  canWrite: boolean;
}

/** The "Ayarlar" tab: channel switches, sender name, reply-to, today's e-mail counter and the test e-mail. */
export function TenantSettingsForm({ settings, canWrite }: TenantSettingsFormProps) {
  const { t } = useTranslation(["notifications", "common"]);
  const organizationName = useAuthStore((state) => state.me?.organization.name) ?? "";
  const save = useUpdateNotificationSettings();
  const [emailEnabled, setEmailEnabled] = useState(settings.email.enabled);
  const [smsEnabled, setSmsEnabled] = useState(settings.sms.enabled);
  const [senderName, setSenderName] = useState(settings.senderName ?? "");
  const [replyTo, setReplyTo] = useState(settings.replyTo ?? "");
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<Partial<Record<Field, string>>>({});

  const errors = validateSettings(senderName.trim(), replyTo.trim());
  const dirty =
    emailEnabled !== settings.email.enabled ||
    smsEnabled !== settings.sms.enabled ||
    senderName !== (settings.senderName ?? "") ||
    replyTo !== (settings.replyTo ?? "");

  const errorText = (field: Field): string | undefined => {
    if (serverErrors[field]) return serverErrors[field];
    const code = errors[field];
    if (!submitted || !code) return undefined;
    return t(`notifications:settings.errors.${code}`, { max: field === "senderName" ? SENDER_NAME_MAX : REPLY_TO_MAX });
  };

  function edit(field: Field, value: string) {
    if (field === "senderName") setSenderName(value);
    else setReplyTo(value);
    setServerErrors((current) => ({ ...current, [field]: undefined }));
  }

  async function onSave() {
    setSubmitted(true);
    setServerErrors({});
    if (Object.keys(errors).length > 0) return;
    const name = senderName.trim();
    const input: NotificationSettingsInput = {
      emailEnabled,
      smsEnabled,
      // The organization name is what an empty sender name shows anyway: do not pin it.
      ...(name && name !== organizationName ? { senderName: name } : {}),
      ...(replyTo.trim() ? { replyTo: replyTo.trim() } : {}),
    };
    try {
      await save.mutateAsync(input);
      toast({ variant: "success", description: t("notifications:settings.saved") });
      setSubmitted(false);
    } catch (error) {
      const fieldErrors: Partial<Record<Field, string>> = {};
      for (const [path, messages] of Object.entries(getApiProblem(error)?.errors ?? {})) {
        const key = path.toLowerCase() === "sendername" ? "senderName" : path.toLowerCase() === "replyto" ? "replyTo" : undefined;
        if (key && messages[0]) fieldErrors[key] = messages[0];
      }
      if (Object.keys(fieldErrors).length > 0) setServerErrors(fieldErrors);
      else toastApiError(error);
    }
  }

  const usage = settings.usage;
  return (
    <Stack gap="md" maw={760}>
      <Card withBorder padding="lg">
        <Stack gap="lg">
          <ChannelRow
            channel="email"
            state={settings.email}
            checked={emailEnabled}
            onChange={setEmailEnabled}
            canWrite={canWrite}
          />
          <ChannelRow
            channel="sms"
            state={settings.sms}
            checked={smsEnabled}
            onChange={setSmsEnabled}
            canWrite={canWrite}
          />
        </Stack>
      </Card>

      <Card withBorder padding="lg">
        <Stack gap="md">
          <TextInput
            label={t("notifications:settings.senderName")}
            description={t("notifications:settings.senderNameHint")}
            placeholder={organizationName}
            value={senderName}
            readOnly={!canWrite}
            onChange={(event) => edit("senderName", event.currentTarget.value)}
            error={errorText("senderName")}
          />
          <TextInput
            label={t("notifications:settings.replyTo")}
            description={t("notifications:settings.replyToHint")}
            type="email"
            value={replyTo}
            readOnly={!canWrite}
            onChange={(event) => edit("replyTo", event.currentTarget.value)}
            error={errorText("replyTo")}
          />
          {canWrite && (
            <Group justify="flex-end">
              <Button onClick={() => void onSave()} disabled={!dirty} loading={save.isPending}>
                {t("common:save")}
              </Button>
            </Group>
          )}
        </Stack>
      </Card>

      <Card withBorder padding="lg">
        <Stack gap="sm">
          <Text fw={600}>{t("notifications:settings.usage.title")}</Text>
          <Text size="sm" data-testid="email-usage">
            {usage.dailyEmailLimit !== undefined
              ? t("notifications:settings.usage.withLimit", {
                  count: formatNumber(usage.emailsToday),
                  limit: formatNumber(usage.dailyEmailLimit),
                })
              : t("notifications:settings.usage.noLimit", { count: formatNumber(usage.emailsToday) })}
          </Text>
          {canWrite && (
            <>
              <Text size="sm" c="dimmed">
                {t("notifications:settings.test.hint")}
              </Text>
              <TestEmailButton availableByPlatform={settings.email.availableByPlatform} />
            </>
          )}
        </Stack>
      </Card>
    </Stack>
  );
}
