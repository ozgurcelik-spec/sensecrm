import { useMemo, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Button, Group, List, Skeleton, Stack, Text } from "@mantine/core";
import { ArrowLeft, Info, TriangleAlert } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { PreferencesMatrix } from "@/components/notifications/preferences-matrix";
import { PageHeader } from "@/components/page-header";
import { useAccessLevel } from "@/hooks/use-module-enabled";
import { useNotificationPreferences, useUpdateNotificationPreferences } from "@/hooks/use-notifications";
import { toast } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import { isModuleOn } from "@/lib/entitlements";
import { cellKey, isCellEditable } from "@/lib/notification-preferences";
import { NOTIFICATION_MANDATORY_PREFERENCE, notificationErrorMessage } from "@/lib/notifications";
import { useAuthStore } from "@/store/auth.store";
import {
  GATED_MODULES,
  NOTIFICATION_CHANNELS,
  type GatedModule,
  type NotificationChannel,
  type NotificationPreferenceChange,
  type NotificationPreferenceKind,
  type NotificationPreferences,
} from "@/types";

interface FormError {
  message: string;
  fields: string[];
}

/** Kinds the user can see: the groups of a module the plan switches off are left out. */
function visibleKinds(
  preferences: NotificationPreferences,
  subscription: Parameters<typeof isModuleOn>[0]
): NotificationPreferenceKind[] {
  return preferences.kinds.filter((kind) => {
    const module = kind.module;
    return !module || !(GATED_MODULES as readonly string[]).includes(module) || isModuleOn(subscription, module as GatedModule);
  });
}

function PreferencesEditor({ preferences }: { preferences: NotificationPreferences }) {
  const { t } = useTranslation(["notifications", "common"]);
  const subscription = useAuthStore((state) => state.me?.subscription);
  const readOnly = useAccessLevel() !== "full";
  const save = useUpdateNotificationPreferences();
  const [draft, setDraft] = useState<Record<string, boolean>>({});
  const [formError, setFormError] = useState<FormError | undefined>();
  const kinds = useMemo(() => visibleKinds(preferences, subscription), [preferences, subscription]);

  // Only cells that differ from what the server has are sent.
  const changes = useMemo(() => {
    const result: NotificationPreferenceChange[] = [];
    for (const kind of kinds) {
      for (const channel of NOTIFICATION_CHANNELS) {
        const cell = kind.channels[channel];
        if (!isCellEditable(preferences, channel, cell)) continue;
        const value = draft[cellKey(kind.kind, channel)];
        if (value !== undefined && value !== cell.enabled) {
          result.push({ kind: kind.kind, channel, enabled: value });
        }
      }
    }
    return result;
  }, [kinds, draft, preferences]);

  function setCell(kind: string, channel: NotificationChannel, enabled: boolean) {
    setDraft((current) => ({ ...current, [cellKey(kind, channel)]: enabled }));
    setFormError(undefined);
  }

  function setMany(channels: readonly NotificationChannel[], valueOf: (cell: { default: boolean }) => boolean) {
    setDraft((current) => {
      const next = { ...current };
      for (const kind of kinds) {
        for (const channel of channels) {
          const cell = kind.channels[channel];
          if (isCellEditable(preferences, channel, cell)) next[cellKey(kind.kind, channel)] = valueOf(cell);
        }
      }
      return next;
    });
    setFormError(undefined);
  }

  async function onSave() {
    setFormError(undefined);
    try {
      await save.mutateAsync(changes);
      setDraft({});
      toast({ variant: "success", description: t("notifications:preferences.saved") });
    } catch (error) {
      const fields = Object.entries(getApiProblem(error)?.errors ?? {}).flatMap(([path, messages]) =>
        messages.map((message) => `${path}: ${message}`)
      );
      setFormError({ message: notificationErrorMessage(error), fields });
      // A refused mandatory cell means the screen was out of date: the refetched server matrix wins.
      if (getApiProblem(error)?.code === NOTIFICATION_MANDATORY_PREFERENCE) setDraft({});
    }
  }

  const emailIssue = preferences.channels.email.addressIssue;
  return (
    <Stack gap="md">
      {readOnly && (
        <Alert color="blue" variant="light" icon={<Info size={16} />} data-testid="prefs-readonly">
          {t("notifications:preferences.readOnly")}
        </Alert>
      )}
      {emailIssue && (
        <Alert color="orange" variant="light" icon={<TriangleAlert size={16} />} data-testid="address-issue">
          {t("notifications:preferences.addressIssue")}
        </Alert>
      )}
      {formError && (
        <Alert color="red" variant="light" role="alert" title={formError.message}>
          {formError.fields.length > 0 && (
            <List size="sm" withPadding>
              {formError.fields.map((field) => (
                <List.Item key={field}>{field}</List.Item>
              ))}
            </List>
          )}
        </Alert>
      )}
      <Text size="sm" c="dimmed">
        {t("notifications:preferences.intro")}
      </Text>
      <PreferencesMatrix
        preferences={preferences}
        kinds={kinds}
        draft={draft}
        onChange={setCell}
        disabled={readOnly}
      />
      <Group justify="space-between" wrap="wrap">
        <Group gap="sm">
          <Button
            variant="default"
            disabled={readOnly}
            onClick={() => setMany(NOTIFICATION_CHANNELS, (cell) => cell.default)}
          >
            {t("notifications:preferences.resetDefaults")}
          </Button>
          <Button variant="default" disabled={readOnly} onClick={() => setMany(["email"], () => false)}>
            {t("notifications:preferences.emailsOff")}
          </Button>
        </Group>
        <Button onClick={() => void onSave()} disabled={readOnly || changes.length === 0} loading={save.isPending}>
          {t("common:save")}
        </Button>
      </Group>
    </Stack>
  );
}

/** "Bildirim tercihleri": the caller's kind x channel matrix. Mandatory cells are locked. */
export default function NotificationPreferencesPage() {
  const { t } = useTranslation(["notifications"]);
  const { data, isLoading, error, refetch } = useNotificationPreferences();
  return (
    <>
      <PageHeader
        title={t("notifications:preferences.title")}
        description={t("notifications:preferences.description")}
        actions={
          <Button component={Link} to="/app/notifications" variant="default" leftSection={<ArrowLeft size={16} />}>
            {t("notifications:preferences.back")}
          </Button>
        }
      />
      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading || !data ? (
        <Skeleton h={320} />
      ) : (
        <PreferencesEditor preferences={data} />
      )}
    </>
  );
}
