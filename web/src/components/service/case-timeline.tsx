import { useTranslation } from "react-i18next";
import { Badge, Button, Card, Group, Skeleton, Stack, Text } from "@mantine/core";
import { LoadError } from "@/components/load-error";
import { useCaseTimeline } from "@/hooks/use-cases";
import { formatDateTime } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import type { TimelineItem } from "@/types";

function TimelineComment({ item, timeZone }: { item: TimelineItem; timeZone?: string }) {
  const { t } = useTranslation(["service"]);
  const internal = item.visibility === "internal";
  return (
    <Card
      withBorder
      padding="sm"
      data-testid="timeline-item"
      data-type="comment"
      data-visibility={item.visibility}
      style={internal ? { background: "var(--mantine-color-yellow-light)" } : undefined}
    >
      <Group justify="space-between" mb={4} wrap="wrap" gap="xs">
        <Group gap="xs">
          <Text size="sm" fw={600}>
            {item.actorName ?? t("service:timeline.unknownUser")}
          </Text>
          {internal ? (
            <Badge size="sm" color="yellow" variant="filled">
              {t("service:timeline.internal")}
            </Badge>
          ) : (
            <Badge size="sm" color="gray" variant="light">
              {t("service:timeline.public")}
            </Badge>
          )}
        </Group>
        <Text size="xs" c="dimmed">
          {formatDateTime(item.occurredAt, timeZone)}
        </Text>
      </Group>
      <Text size="sm" style={{ whiteSpace: "pre-wrap", wordBreak: "break-word" }}>
        {item.body}
      </Text>
    </Card>
  );
}

function TimelineEvent({ item, timeZone }: { item: TimelineItem; timeZone?: string }) {
  const { t } = useTranslation(["service"]);
  const status = (value?: string) =>
    value ? t(`service:statuses.${value}`, { defaultValue: value }) : "-";
  const priority = (value?: string) =>
    value ? t(`service:priorities.${value}`, { defaultValue: value }) : "-";
  const user = (name?: string) => name ?? t("service:unassigned");

  let text: string;
  switch (item.type) {
    case "created":
      text = t("service:timeline.created");
      break;
    case "statusChanged":
      text = t("service:timeline.statusChanged", { from: status(item.from), to: status(item.to) });
      break;
    case "priorityChanged":
      text = t("service:timeline.priorityChanged", {
        from: priority(item.from),
        to: priority(item.to),
      });
      break;
    case "assigned":
      text = t("service:timeline.assigned", { from: user(item.fromName), to: user(item.toName) });
      break;
    default:
      text = item.type;
  }

  return (
    <Group
      gap="xs"
      wrap="nowrap"
      align="flex-start"
      px="xs"
      data-testid="timeline-item"
      data-type={item.type}
    >
      <Text size="sm" c="dimmed" style={{ flex: 1, minWidth: 0 }}>
        <Text component="span" size="sm" fw={500} c="var(--mantine-color-text)">
          {item.actorName ?? t("service:timeline.unknownUser")}
        </Text>{" "}
        {text}
        {item.note && (
          <Text component="span" size="sm" fs="italic" style={{ display: "block" }}>
            {t("service:timeline.note", { note: item.note })}
          </Text>
        )}
      </Text>
      <Text size="xs" c="dimmed" style={{ whiteSpace: "nowrap" }}>
        {formatDateTime(item.occurredAt, timeZone)}
      </Text>
    </Group>
  );
}

/** Merged comments and events of a case, newest first, with "show more" paging. */
export function CaseTimeline({ caseId }: { caseId: string }) {
  const { t } = useTranslation(["service"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const timeline = useCaseTimeline(caseId);

  if (timeline.error && !timeline.data) {
    return <LoadError error={timeline.error} onRetry={() => void timeline.refetch()} />;
  }
  if (timeline.isLoading) {
    return (
      <Stack gap="sm">
        <Skeleton h={56} />
        <Skeleton h={56} />
      </Stack>
    );
  }
  const items = (timeline.data?.pages ?? []).flatMap((p) => p.items);
  if (items.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="lg">
        {t("service:timeline.empty")}
      </Text>
    );
  }

  return (
    <Stack gap="sm" component="ol" p={0} style={{ listStyle: "none", margin: 0 }}>
      {items.map((item) => (
        <li key={`${item.type}-${item.id}`}>
          {item.type === "comment" ? (
            <TimelineComment item={item} timeZone={timeZone} />
          ) : (
            <TimelineEvent item={item} timeZone={timeZone} />
          )}
        </li>
      ))}
      {timeline.hasNextPage && (
        <li>
          <Button
            variant="default"
            fullWidth
            loading={timeline.isFetchingNextPage}
            onClick={() => void timeline.fetchNextPage()}
          >
            {t("service:timeline.showMore")}
          </Button>
        </li>
      )}
    </Stack>
  );
}
