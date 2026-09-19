/** Read-only building blocks shared by the quote and order detail pages and the editor. */
import { useTranslation } from "react-i18next";
import { Card, Group, Stack, Table, Text } from "@mantine/core";
import { formatMoney, formatNumber, orDash } from "@/lib/format";
import type { DocumentLine } from "@/types";

export interface TotalsValues {
  subtotal: number;
  discountTotal: number;
  taxTotal: number;
  grandTotal: number;
}

interface TotalsCardProps {
  totals: TotalsValues;
  currency: string;
  /** Marks the card as the live preview computed in the browser (before saving). */
  preview?: boolean;
}

/** Subtotal / discount / tax / grand total. */
export function TotalsCard({ totals, currency, preview = false }: TotalsCardProps) {
  const { t } = useTranslation(["commerce"]);
  const rows: { key: string; label: string; value: number; strong?: boolean }[] = [
    { key: "subtotal", label: t("commerce:totals.subtotal"), value: totals.subtotal },
    { key: "discount", label: t("commerce:totals.discount"), value: -totals.discountTotal },
    { key: "tax", label: t("commerce:totals.tax"), value: totals.taxTotal },
    { key: "grand", label: t("commerce:totals.grandTotal"), value: totals.grandTotal, strong: true },
  ];
  return (
    <Card withBorder padding="md" maw={360} ml="auto" data-testid="totals-card" aria-live="polite">
      <Stack gap={6}>
        {rows.map((row) => (
          <Group key={row.key} justify="space-between" wrap="nowrap">
            <Text size={row.strong ? "md" : "sm"} fw={row.strong ? 700 : 400}>
              {row.label}
            </Text>
            <Text
              size={row.strong ? "md" : "sm"}
              fw={row.strong ? 700 : 400}
              data-testid={`total-${row.key}`}
            >
              {formatMoney(row.value, currency)}
            </Text>
          </Group>
        ))}
        {preview && (
          <Text size="xs" c="dimmed">
            {t("commerce:totals.previewNote")}
          </Text>
        )}
      </Stack>
    </Card>
  );
}

/** Line table of a saved document (server values). */
export function LinesTable({ lines, currency }: { lines: readonly DocumentLine[]; currency: string }) {
  const { t } = useTranslation(["commerce"]);
  if (lines.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="md">
        {t("commerce:lines.empty")}
      </Text>
    );
  }
  return (
    <Table.ScrollContainer minWidth={720}>
      <Table verticalSpacing="xs" highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th w={40}>#</Table.Th>
            <Table.Th>{t("commerce:lines.description")}</Table.Th>
            <Table.Th ta="right">{t("commerce:lines.quantity")}</Table.Th>
            <Table.Th ta="right">{t("commerce:lines.unitPrice")}</Table.Th>
            <Table.Th ta="right">{t("commerce:lines.discountPercent")}</Table.Th>
            <Table.Th ta="right">{t("commerce:lines.taxRate")}</Table.Th>
            <Table.Th ta="right">{t("commerce:lines.lineTotal")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {[...lines]
            .sort((a, b) => a.position - b.position)
            .map((line, index) => (
              <Table.Tr key={line.id}>
                <Table.Td>{index + 1}</Table.Td>
                <Table.Td style={{ wordBreak: "break-word" }}>{line.description}</Table.Td>
                <Table.Td ta="right">{formatNumber(line.quantity)}</Table.Td>
                <Table.Td ta="right">{formatMoney(line.unitPrice, currency)}</Table.Td>
                <Table.Td ta="right">{`${formatNumber(line.discountPercent)}%`}</Table.Td>
                <Table.Td ta="right">{`${formatNumber(line.taxRate)}%`}</Table.Td>
                <Table.Td ta="right" fw={600}>
                  {formatMoney(line.lineTotal, currency)}
                </Table.Td>
              </Table.Tr>
            ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/** Terms and notes cards of a document. */
export function TermsAndNotes({ terms, notes }: { terms?: string; notes?: string }) {
  const { t } = useTranslation(["commerce"]);
  return (
    <>
      <Card withBorder padding="md">
        <Text fw={600} mb="sm">
          {t("commerce:fields.terms")}
        </Text>
        <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
          {orDash(terms)}
        </Text>
      </Card>
      <Card withBorder padding="md">
        <Text fw={600} mb="sm">
          {t("commerce:fields.notes")}
        </Text>
        <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
          {orDash(notes)}
        </Text>
      </Card>
    </>
  );
}
