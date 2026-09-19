import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Card, Group, Skeleton, Text } from "@mantine/core";
import { getApiErrorMessage } from "@/lib/api-error";

interface DashboardWidgetProps {
  title: string;
  /** Right-aligned header content (a link). */
  action?: ReactNode;
  isLoading: boolean;
  error?: unknown;
  onRetry?: () => void;
  isEmpty?: boolean;
  emptyMessage?: string;
  /** Skeleton height while loading. */
  skeletonHeight?: number;
  children: ReactNode;
  testId?: string;
}

/** Card frame of a dashboard widget with the shared loading, error (retry) and empty states. */
export function DashboardWidget({
  title,
  action,
  isLoading,
  error,
  onRetry,
  isEmpty = false,
  emptyMessage,
  skeletonHeight = 160,
  children,
  testId,
}: DashboardWidgetProps) {
  const { t } = useTranslation(["common", "home"]);
  let body: ReactNode;
  if (error) {
    body = (
      <Alert color="red" variant="light" title={t("home:charts.failed")}>
        <Group justify="space-between" wrap="nowrap">
          {getApiErrorMessage(error)}
          {onRetry && (
            <Button size="xs" variant="default" onClick={onRetry}>
              {t("common:retry")}
            </Button>
          )}
        </Group>
      </Alert>
    );
  } else if (isLoading) {
    body = <Skeleton h={skeletonHeight} data-testid="widget-skeleton" />;
  } else if (isEmpty) {
    body = (
      <Text size="sm" c="dimmed" ta="center" py="lg">
        {emptyMessage ?? t("home:charts.empty")}
      </Text>
    );
  } else {
    body = children;
  }

  return (
    <Card withBorder padding="lg" data-testid={testId}>
      <Group justify="space-between" mb="md" wrap="nowrap">
        <Text fw={600}>{title}</Text>
        {action}
      </Group>
      {body}
    </Card>
  );
}
