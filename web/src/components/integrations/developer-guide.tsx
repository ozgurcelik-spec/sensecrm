import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Accordion, Badge, Button, Card, Group, List, Skeleton, Stack, Table, Tabs, Text, Title } from "@mantine/core";
import { Download } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { useIntegrationsStatus, useWebhookEvents } from "@/hooks/use-integrations";
import { toastApiError } from "@/hooks/use-toast";
import { formatCalendarDate } from "@/lib/format";
import { apiBaseUrl, curlExample, eventTypeLabel } from "@/lib/integrations";
import {
  ENVELOPE_EXAMPLE,
  ERROR_EXAMPLE,
  PAGING_EXAMPLE,
  REQUEST_HEADERS,
  SIGNATURE_EXAMPLE,
  VERIFY_SAMPLES,
} from "@/lib/integrations-guide";
import { downloadBlob } from "@/lib/platform";
import { downloadOpenApiDocument } from "@/services/integrations.service";
import { WEBHOOK_EVENT_GROUPS } from "@/types";
import { CodeBlock } from "./code-block";

function Section({ title, children, testId }: { title: string; children: ReactNode; testId?: string }) {
  return (
    <Card withBorder padding="lg" data-testid={testId}>
      <Title order={4} mb="sm">
        {title}
      </Title>
      <Stack gap="sm">{children}</Stack>
    </Card>
  );
}

/** "Download OpenAPI": the endpoint needs the Bearer header, so it is fetched and saved as a blob (a plain link cannot work). */
function OpenApiDownload() {
  const { t } = useTranslation(["integrations"]);
  const [loading, setLoading] = useState(false);

  async function onDownload() {
    setLoading(true);
    try {
      const blob = await downloadOpenApiDocument();
      downloadBlob("openapi.json", blob);
    } catch (error) {
      // plan.module_disabled / forbidden / tenant state codes are worded by getApiErrorMessage.
      toastApiError(error);
    } finally {
      setLoading(false);
    }
  }

  return (
    <Group>
      <Button leftSection={<Download size={16} />} loading={loading} onClick={() => void onDownload()}>
        {t("integrations:guide.openapi.download")}
      </Button>
    </Group>
  );
}

function EventCatalog() {
  const { t } = useTranslation(["integrations"]);
  const { data, isLoading, error, refetch } = useWebhookEvents();
  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading || !data) return <Skeleton h={120} />;
  return (
    <Accordion variant="contained" data-testid="event-catalog">
      {WEBHOOK_EVENT_GROUPS.flatMap((group) => data.filter((event) => event.group === group)).map((event) => (
        <Accordion.Item key={event.type} value={event.type}>
          <Accordion.Control aria-label={t("integrations:guide.events.itemNamed", { type: event.type })}>
            <Group gap="xs" wrap="wrap">
              <Text size="sm" ff="monospace" fw={600}>
                {event.type}
              </Text>
              <Text size="sm" c="dimmed">
                {eventTypeLabel(event.type)}
              </Text>
              <Badge size="xs" variant="light" color="gray">
                {t(`integrations:events.groups.${event.group}`)}
              </Badge>
              {!event.available && (
                <Badge size="xs" variant="light" color="orange">
                  {t("integrations:events.unavailable")}
                </Badge>
              )}
              {event.deprecated && (
                <Badge size="xs" variant="light" color="red">
                  {event.sunsetOn
                    ? t("integrations:guide.events.deprecatedUntil", { date: formatCalendarDate(event.sunsetOn) })
                    : t("integrations:events.deprecated")}
                </Badge>
              )}
            </Group>
          </Accordion.Control>
          <Accordion.Panel>
            {event.description && (
              <Text size="sm" mb="xs">
                {event.description}
              </Text>
            )}
            <CodeBlock
              code={JSON.stringify(event.sample, null, 2)}
              label={t("integrations:events.sample")}
              testId={`catalog-sample-${event.type}`}
            />
          </Accordion.Panel>
        </Accordion.Item>
      ))}
    </Accordion>
  );
}

/**
 * Developer tab: how to call the API with a key (curl, scopes, paging, errors, rate limit, version
 * policy), the webhook guide (envelope, headers, signature verification in Node / Python / C#,
 * retries, de-duplication, network notes), the live event catalog with sample envelopes and the
 * OpenAPI download.
 */
export function DeveloperGuide() {
  const { t } = useTranslation(["integrations"]);
  const status = useIntegrationsStatus();
  const s = status.data;
  const tolerance = s?.signatureToleranceSeconds ?? 300;
  const base = apiBaseUrl();

  return (
    <Stack gap="md" maw={900} data-testid="developer-guide">
      <Section title={t("integrations:guide.api.title")} testId="guide-api">
        <Text size="sm">{t("integrations:guide.api.intro", { base })}</Text>
        <CodeBlock code={curlExample("crmk_<...>", base)} label={t("integrations:guide.api.curl")} testId="guide-curl" />
        <List size="sm" spacing={4}>
          <List.Item>{t("integrations:guide.api.scopes")}</List.Item>
          <List.Item>{t("integrations:guide.api.lifetime", { max: s?.apiKeys.maxLifetimeDays ?? 730 })}</List.Item>
          <List.Item>{t("integrations:guide.api.rateLimit", { limit: s?.apiKeys.rateLimitPerMinute ?? 120 })}</List.Item>
          <List.Item>{t("integrations:guide.api.actAsCreator")}</List.Item>
        </List>
      </Section>

      <Section title={t("integrations:guide.conventions.title")} testId="guide-conventions">
        <Text size="sm">{t("integrations:guide.conventions.paging")}</Text>
        <CodeBlock code={PAGING_EXAMPLE} label={t("integrations:guide.conventions.pagingExample")} />
        <Text size="sm">{t("integrations:guide.conventions.errors")}</Text>
        <CodeBlock code={ERROR_EXAMPLE} label={t("integrations:guide.conventions.errorExample")} />
        <Text size="sm">{t("integrations:guide.conventions.versioning")}</Text>
      </Section>

      <Section title={t("integrations:guide.webhooks.title")} testId="guide-webhooks">
        <Text size="sm">{t("integrations:guide.webhooks.intro")}</Text>
        <CodeBlock code={ENVELOPE_EXAMPLE} label={t("integrations:guide.webhooks.envelope")} testId="guide-envelope" />
        <Text size="sm">{t("integrations:guide.webhooks.versionPolicy")}</Text>
        <Text size="sm" fw={600}>
          {t("integrations:guide.webhooks.headers")}
        </Text>
        <Table fz="sm" verticalSpacing={4} data-testid="guide-headers">
          <Table.Tbody>
            {REQUEST_HEADERS.map((header) => (
              <Table.Tr key={header.name}>
                <Table.Td ff="monospace" style={{ whiteSpace: "nowrap" }}>
                  {header.name}
                </Table.Td>
                <Table.Td>{t(`integrations:guide.webhooks.headerDescriptions.${header.key}`)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        <Text size="sm" fw={600}>
          {t("integrations:guide.webhooks.signatureTitle")}
        </Text>
        <Text size="sm">{t("integrations:guide.webhooks.signatureRule", { seconds: tolerance })}</Text>
        <CodeBlock code={SIGNATURE_EXAMPLE} label={t("integrations:guide.webhooks.signatureExample")} />
        <Tabs defaultValue="node" keepMounted={false}>
          <Tabs.List aria-label={t("integrations:guide.webhooks.verifyTabs")}>
            {VERIFY_SAMPLES.map((sample) => (
              <Tabs.Tab key={sample.key} value={sample.key}>
                {sample.label}
              </Tabs.Tab>
            ))}
          </Tabs.List>
          {VERIFY_SAMPLES.map((sample) => (
            <Tabs.Panel key={sample.key} value={sample.key} pt="sm">
              <CodeBlock code={sample.code} label={t("integrations:guide.webhooks.verifyNamed", { lang: sample.label })} testId={`verify-${sample.key}`} />
            </Tabs.Panel>
          ))}
        </Tabs>
        <Text size="sm" fw={600}>
          {t("integrations:guide.webhooks.retryTitle")}
        </Text>
        <Text size="sm" data-testid="guide-retry">
          {t("integrations:guide.webhooks.retry", {
            attempts: s?.maxAttempts ?? 8,
            timeout: s?.timeoutSeconds ?? 10,
            days: s?.deliveryRetentionDays ?? 30,
          })}
        </Text>
        <List size="sm" spacing={4}>
          <List.Item>{t("integrations:guide.webhooks.dedupe")}</List.Item>
          <List.Item>{t("integrations:guide.webhooks.unknownFields")}</List.Item>
          <List.Item>{t("integrations:guide.webhooks.network")}</List.Item>
          <List.Item>{t("integrations:guide.webhooks.fetchDetails")}</List.Item>
        </List>
      </Section>

      <Section title={t("integrations:guide.events.title")} testId="guide-events">
        <Text size="sm">{t("integrations:guide.events.intro")}</Text>
        <EventCatalog />
      </Section>

      <Section title={t("integrations:guide.openapi.title")} testId="guide-openapi">
        <Text size="sm">{t("integrations:guide.openapi.intro")}</Text>
        <OpenApiDownload />
      </Section>
    </Stack>
  );
}
