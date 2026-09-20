import { Fragment } from "react";
import { useTranslation } from "react-i18next";
import { Badge, Card, Group, Stack, Switch, Table, Text, Tooltip } from "@mantine/core";
import { Lock } from "lucide-react";
import { cellValue, isCellEditable } from "@/lib/notification-preferences";
import { channelLabel, groupLabel, groupPreferenceKinds, kindLabel } from "@/lib/notifications";
import {
  NOTIFICATION_CHANNELS,
  type NotificationChannel,
  type NotificationPreferenceKind,
  type NotificationPreferences,
} from "@/types";

interface PreferencesMatrixProps {
  preferences: NotificationPreferences;
  /** The kinds to show (groups of switched-off modules are already removed). */
  kinds: readonly NotificationPreferenceKind[];
  draft: Readonly<Record<string, boolean>>;
  onChange: (kind: string, channel: NotificationChannel, enabled: boolean) => void;
  /** Whole matrix disabled (read-only tenant). */
  disabled?: boolean;
}

/** Rows = notification kinds (grouped), columns = channels. SMS only appears when the platform offers it. */
export function PreferencesMatrix({ preferences, kinds, draft, onChange, disabled = false }: PreferencesMatrixProps) {
  const { t } = useTranslation(["notifications"]);
  const channels = NOTIFICATION_CHANNELS.filter((channel) => channel !== "sms" || preferences.channels.sms.available);
  const groups = groupPreferenceKinds(kinds);

  return (
    <Card withBorder padding={0}>
      <Table.ScrollContainer minWidth={560}>
        <Table verticalSpacing="sm" data-testid="preferences-matrix">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t("notifications:preferences.columns.kind")}</Table.Th>
              {channels.map((channel) => {
                const state = preferences.channels[channel];
                return (
                  <Table.Th key={channel} ta="center" w={140}>
                    <Stack gap={0} align="center">
                      <span>{channelLabel(channel)}</span>
                      {!state.available && state.reason && (
                        <Text size="xs" c="dimmed" fw={400} data-testid={`unavailable-${channel}`}>
                          {t(`notifications:preferences.unavailable.${state.reason}`)}
                        </Text>
                      )}
                    </Stack>
                  </Table.Th>
                );
              })}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {groups.map(({ group, kinds: groupKinds }) => (
              <Fragment key={group}>
                <Table.Tr bg="var(--mantine-color-default-hover)">
                  <Table.Td colSpan={channels.length + 1}>
                    <Text size="xs" fw={700} tt="uppercase" c="dimmed">
                      {groupLabel(group)}
                    </Text>
                  </Table.Td>
                </Table.Tr>
                {groupKinds.map((kind) => (
                  <Table.Tr key={kind.kind} data-testid={`pref-row-${kind.kind}`}>
                    <Table.Td>
                      <Group gap={6} wrap="nowrap">
                        <div>
                          <Text size="sm" fw={500}>
                            {kindLabel(kind.kind)}
                          </Text>
                          <Text size="xs" c="dimmed">
                            {t(`notifications:kinds.${kind.kind}.description`, { defaultValue: "" })}
                          </Text>
                        </div>
                        {kind.mandatory && (
                          <Badge size="xs" variant="light" color="gray">
                            {t("notifications:mandatoryBadge")}
                          </Badge>
                        )}
                      </Group>
                    </Table.Td>
                    {channels.map((channel) => {
                      const cell = kind.channels[channel];
                      const label = `${kindLabel(kind.kind)} - ${channelLabel(channel)}`;
                      if (cell.supported === false) {
                        return (
                          <Table.Td key={channel} ta="center">
                            <Text size="sm" c="dimmed" aria-label={t("notifications:preferences.unsupported")}>
                              –
                            </Text>
                          </Table.Td>
                        );
                      }
                      const editable = isCellEditable(preferences, channel, cell);
                      return (
                        <Table.Td key={channel} ta="center">
                          <Group gap={6} justify="center" wrap="nowrap">
                            <Switch
                              aria-label={cell.locked ? `${label} (${t("notifications:preferences.locked")})` : label}
                              checked={cellValue(draft, kind.kind, channel, cell)}
                              disabled={disabled || !editable}
                              onChange={(event) => onChange(kind.kind, channel, event.currentTarget.checked)}
                            />
                            {cell.locked && (
                              <Tooltip label={t("notifications:preferences.lockedHint")}>
                                <Lock size={14} aria-hidden="true" />
                              </Tooltip>
                            )}
                          </Group>
                        </Table.Td>
                      );
                    })}
                  </Table.Tr>
                ))}
              </Fragment>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
    </Card>
  );
}
