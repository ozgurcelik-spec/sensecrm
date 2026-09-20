import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Badge, Button, Checkbox, Fieldset, Group, Input, Skeleton, Stack, Text } from "@mantine/core";
import { LoadError } from "@/components/load-error";
import { useWebhookEvents } from "@/hooks/use-integrations";
import { eventTypeLabel } from "@/lib/integrations";
import { WEBHOOK_EVENT_GROUPS, type WebhookEventInfo } from "@/types";
import { CodeBlock } from "./code-block";

interface EventTypePickerProps {
  value: string[];
  onChange: (value: string[]) => void;
  error?: string;
  disabled?: boolean;
}

/**
 * Event catalog picker (`GET /integrations/webhook-events`): checkboxes grouped by Sales / Commerce
 * / Service. A type whose source module the plan lacks (`available: false`) is dimmed with a hint and
 * cannot be added (an already selected one can still be removed). Each type has a sample envelope.
 */
export function EventTypePicker({ value, onChange, error, disabled = false }: EventTypePickerProps) {
  const { t } = useTranslation(["integrations"]);
  const { data, isLoading, error: loadError, refetch } = useWebhookEvents();
  const [preview, setPreview] = useState<string | null>(null);
  const selected = new Set(value);

  function toggle(keys: string[], checked: boolean) {
    const next = new Set(selected);
    for (const key of keys) {
      if (checked) next.add(key);
      else next.delete(key);
    }
    // Keep the catalog order so the request body is stable; types the catalog no longer lists stay.
    const order = (data ?? []).map((event) => event.type);
    onChange([
      ...order.filter((type) => next.has(type)),
      ...[...next].filter((type) => !order.includes(type)),
    ]);
  }

  if (loadError) return <LoadError error={loadError} onRetry={() => void refetch()} />;
  if (isLoading || !data) return <Skeleton h={160} />;

  return (
    <Input.Wrapper
      label={t("integrations:webhooks.form.eventTypes")}
      description={t("integrations:webhooks.form.eventTypesHint")}
      withAsterisk
      error={error}
    >
      <Stack gap="sm" mt="xs" data-testid="event-type-picker">
        {WEBHOOK_EVENT_GROUPS.map((group) => {
          const events = data.filter((event) => event.group === group);
          if (events.length === 0) return null;
          const addable = events.filter((event) => event.available).map((event) => event.type);
          const count = addable.filter((type) => selected.has(type)).length;
          return (
            <Fieldset
              key={group}
              legend={
                <Checkbox
                  label={t(`integrations:events.groups.${group}`)}
                  checked={addable.length > 0 && count === addable.length}
                  indeterminate={count > 0 && count < addable.length}
                  disabled={disabled || addable.length === 0}
                  onChange={(event) => toggle(addable, event.currentTarget.checked)}
                  fw={600}
                />
              }
            >
              <Stack gap="xs">
                {events.map((event) => (
                  <EventRow
                    key={event.type}
                    event={event}
                    checked={selected.has(event.type)}
                    disabled={disabled || (!event.available && !selected.has(event.type))}
                    previewOpen={preview === event.type}
                    onTogglePreview={() => setPreview(preview === event.type ? null : event.type)}
                    onChange={(checked) => toggle([event.type], checked)}
                  />
                ))}
              </Stack>
            </Fieldset>
          );
        })}
      </Stack>
    </Input.Wrapper>
  );
}

interface EventRowProps {
  event: WebhookEventInfo;
  checked: boolean;
  disabled: boolean;
  previewOpen: boolean;
  onTogglePreview: () => void;
  onChange: (checked: boolean) => void;
}

function EventRow({ event, checked, disabled, previewOpen, onTogglePreview, onChange }: EventRowProps) {
  const { t } = useTranslation(["integrations"]);
  return (
    <Stack gap={4} style={{ opacity: event.available ? 1 : 0.6 }} data-testid={`event-${event.type}`}>
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Checkbox
          label={eventTypeLabel(event.type)}
          description={
            event.available ? (
              <Text span size="xs" ff="monospace">
                {event.type}
              </Text>
            ) : (
              <Text span size="xs" c="orange" data-testid={`unavailable-${event.type}`}>
                {t("integrations:events.unavailable")}
              </Text>
            )
          }
          checked={checked}
          disabled={disabled}
          onChange={(e) => onChange(e.currentTarget.checked)}
        />
        <Group gap="xs" wrap="nowrap">
          {event.deprecated && (
            <Badge size="xs" color="orange" variant="light">
              {t("integrations:events.deprecated")}
            </Badge>
          )}
          <Button
            variant="subtle"
            size="compact-xs"
            aria-expanded={previewOpen}
            aria-label={t("integrations:events.sampleNamed", { type: event.type })}
            onClick={onTogglePreview}
          >
            {previewOpen ? t("integrations:events.hideSample") : t("integrations:events.showSample")}
          </Button>
        </Group>
      </Group>
      {previewOpen && (
        <CodeBlock
          code={JSON.stringify(event.sample, null, 2)}
          label={t("integrations:events.sample")}
          testId={`sample-${event.type}`}
        />
      )}
    </Stack>
  );
}
