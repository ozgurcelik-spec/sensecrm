import type { ReactNode } from "react";
import { Link, useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import {
  Anchor,
  Button,
  Card,
  Grid,
  Group,
  Skeleton,
  Stack,
  Tabs,
  Text,
  Title,
} from "@mantine/core";
import { ArrowLeft } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { getApiErrorStatus } from "@/lib/api-error";

export interface DetailTab {
  value: string;
  label: string;
  content: ReactNode;
}

export interface InfoRow {
  label: string;
  value: ReactNode;
}

/** Left-hand facts panel of a detail page (Zoho style): label above value. */
export function InfoPanel({ title, rows }: { title: string; rows: InfoRow[] }) {
  return (
    <Card withBorder padding="md">
      <Text fw={600} mb="sm">
        {title}
      </Text>
      <Stack gap="sm">
        {rows.map((row) => (
          <div key={row.label}>
            <Text size="xs" c="dimmed">
              {row.label}
            </Text>
            <Text size="sm" component="div" style={{ wordBreak: "break-word" }}>
              {row.value}
            </Text>
          </div>
        ))}
      </Stack>
    </Card>
  );
}

interface RecordDetailShellProps {
  backTo: string;
  backLabel: string;
  title?: string;
  subtitle?: ReactNode;
  badges?: ReactNode;
  actions?: ReactNode;
  isLoading: boolean;
  error?: unknown;
  onRetry: () => void;
  panel?: ReactNode;
  /** Which side the info panel sits on (default left; the case detail keeps it on the right). */
  panelSide?: "left" | "right";
  tabs: DetailTab[];
}

/**
 * Detail page frame: back link, title row with actions, left info panel and tabs on the right.
 * The active tab lives in `?tab=` so a tab can be linked and survives a reload.
 */
export function RecordDetailShell({
  backTo,
  backLabel,
  title,
  subtitle,
  badges,
  actions,
  isLoading,
  error,
  onRetry,
  panel,
  panelSide = "left",
  tabs,
}: RecordDetailShellProps) {
  const { t } = useTranslation(["crm"]);
  const [searchParams, setSearchParams] = useSearchParams();
  const requested = searchParams.get("tab");
  const active = tabs.some((tab) => tab.value === requested)
    ? (requested as string)
    : tabs[0]?.value;

  const notFound = getApiErrorStatus(error) === 404;

  return (
    <Stack gap="md">
      <Anchor component={Link} to={backTo} size="sm">
        <Group gap={4}>
          <ArrowLeft size={14} />
          {backLabel}
        </Group>
      </Anchor>

      {notFound ? (
        <Card withBorder padding="xl">
          <Stack align="center" gap="sm">
            <Title order={3}>{t("crm:notFound.title")}</Title>
            <Text size="sm" c="dimmed">
              {t("crm:notFound.description")}
            </Text>
            <Button component={Link} to={backTo} variant="default">
              {backLabel}
            </Button>
          </Stack>
        </Card>
      ) : error ? (
        <LoadError error={error} onRetry={onRetry} />
      ) : isLoading || !title ? (
        <Stack gap="sm">
          <Skeleton h={36} w={320} />
          <Skeleton h={220} />
        </Stack>
      ) : (
        <>
          <Group justify="space-between" align="flex-start" wrap="wrap" gap="md">
            <Stack gap={4}>
              <Group gap="sm">
                <Title order={2}>{title}</Title>
                {badges}
              </Group>
              {subtitle && (
                <Text size="sm" c="dimmed">
                  {subtitle}
                </Text>
              )}
            </Stack>
            {actions && <Group gap="sm">{actions}</Group>}
          </Group>

          <Grid gap="md">
            <Grid.Col
              span={{ base: 12, md: 4, lg: 3 }}
              order={panelSide === "right" ? { base: 2, md: 2 } : undefined}
            >
              {panel}
            </Grid.Col>
            <Grid.Col
              span={{ base: 12, md: 8, lg: 9 }}
              order={panelSide === "right" ? { base: 1, md: 1 } : undefined}
            >
              <Tabs
                value={active}
                onChange={(value) =>
                  setSearchParams(
                    (prev) => {
                      const next = new URLSearchParams(prev);
                      if (!value || value === tabs[0]?.value) next.delete("tab");
                      else next.set("tab", value);
                      return next;
                    },
                    { replace: true }
                  )
                }
                keepMounted={false}
              >
                <Tabs.List mb="md">
                  {tabs.map((tab) => (
                    <Tabs.Tab key={tab.value} value={tab.value}>
                      {tab.label}
                    </Tabs.Tab>
                  ))}
                </Tabs.List>
                {tabs.map((tab) => (
                  <Tabs.Panel key={tab.value} value={tab.value}>
                    {tab.content}
                  </Tabs.Panel>
                ))}
              </Tabs>
            </Grid.Col>
          </Grid>
        </>
      )}
    </Stack>
  );
}
