import { useTranslation } from "react-i18next";
import { Code, Popover, Table, Text, UnstyledButton } from "@mantine/core";

type Diff = { before: unknown; after: unknown };

/** Accepts `{ old, new }`, `{ oldValue, newValue }` or `{ before, after }` pairs; anything else is a plain value. */
function asDiff(value: unknown): Diff | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const v = value as Record<string, unknown>;
  if ("old" in v || "new" in v) return { before: v.old, after: v.new };
  if ("oldValue" in v || "newValue" in v) return { before: v.oldValue, after: v.newValue };
  if ("before" in v || "after" in v) return { before: v.before, after: v.after };
  return null;
}

function show(value: unknown): string {
  if (value === null || value === undefined || value === "") return "-";
  return typeof value === "string" ? value : JSON.stringify(value);
}

/** Compact "N fields" trigger that opens a field / before / after table. */
export function AuditChanges({ changes }: { changes?: Record<string, unknown> | null }) {
  const { t } = useTranslation(["audit"]);
  const entries = Object.entries(changes ?? {});
  if (entries.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        {t("audit:noChanges")}
      </Text>
    );
  }

  return (
    <Popover width={420} position="bottom-start" shadow="md" withArrow>
      <Popover.Target>
        <UnstyledButton>
          <Text size="sm" c="brand.7" td="underline">
            {t("audit:showChanges", { count: entries.length })}
          </Text>
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown>
        <Table fz="xs" verticalSpacing={4}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t("audit:field")}</Table.Th>
              <Table.Th>{t("audit:changes")}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {entries.map(([field, value]) => {
              const diff = asDiff(value);
              return (
                <Table.Tr key={field}>
                  <Table.Td fw={600}>{field}</Table.Td>
                  <Table.Td style={{ wordBreak: "break-word" }}>
                    {diff ? (
                      <>
                        <Code color="red.1">{show(diff.before)}</Code> {"→"}{" "}
                        <Code color="green.1">{show(diff.after)}</Code>
                      </>
                    ) : (
                      <Code>{show(value)}</Code>
                    )}
                  </Table.Td>
                </Table.Tr>
              );
            })}
          </Table.Tbody>
        </Table>
      </Popover.Dropdown>
    </Popover>
  );
}
