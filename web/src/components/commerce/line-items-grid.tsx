import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActionIcon, Badge, Button, Group, NumberInput, Stack, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { ArrowDown, ArrowUp, Plus, RefreshCw, Trash2 } from "lucide-react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { toastApiError } from "@/hooks/use-toast";
import { checkAdjustment, computeTotals, roundingAdjustment } from "@/lib/commerce-totals";
import { resolvePrices } from "@/services/pricebooks.service";
import {
  MAX_LINES,
  newLine,
  type LineDraft,
  type LineErrors,
  type LineField,
  type LinePriceSource,
} from "@/lib/commerce-lines";
import { formatMoney } from "@/lib/format";
import type { Product } from "@/types";
import { AdjustmentField } from "./adjustment-field";
import { TotalsCard } from "./document-parts";
import { ProductPicker } from "./product-picker";

interface LineItemsGridProps {
  lines: LineDraft[];
  onChange: (lines: LineDraft[]) => void;
  /** Document currency. */
  currency: string;
  /** Cell errors (client check keys or server texts). */
  errors?: LineErrors;
  /** `crm.products.read`: without it the product column is off and lines are typed by hand. */
  canPickProducts: boolean;
  disabled?: boolean;
  /** Which price a picked product brings: the sales price (price book / catalog) or the vendor purchase price. */
  mode?: "sales" | "purchase";
  /**
   * The document's price book. With it (and `crm.pricebooks.read`, decided by the caller) picking a
   * product asks the server for the list price (`POST /pricebooks/{id}/resolve`); the row shows where
   * the price came from.
   */
  priceBookId?: string;
  /** Rounding line (M9C): given together with `onAdjustmentChange` the totals card edits it. */
  adjustment?: number | string;
  onAdjustmentChange?: (value: number | string) => void;
  /** Server text of `errors.adjustment`. */
  adjustmentError?: string;
}

/**
 * Line item grid shared by the quote and order editors: add / remove / move rows, product picker that
 * fills description, unit price and tax rate, per-row totals and the live totals card. Everything shown
 * here is computed in the browser (`computeTotals`); after saving, the server's values are authoritative.
 */
export function LineItemsGrid({
  lines,
  onChange,
  currency,
  errors = {},
  canPickProducts,
  disabled = false,
  mode = "sales",
  priceBookId,
  adjustment = 0,
  onAdjustmentChange,
  adjustmentError,
}: LineItemsGridProps) {
  const { t } = useTranslation(["commerce"]);
  const totals = useMemo(() => computeTotals(lines, adjustment), [lines, adjustment]);
  const adjustmentIssue = useMemo(() => checkAdjustment(adjustment, lines), [adjustment, lines]);
  const [repricing, setRepricing] = useState<"idle" | "confirm" | "running">("idle");
  // Async price answers apply to the rows as they are by then, not as they were when asked.
  const linesRef = useRef(lines);
  useEffect(() => {
    linesRef.current = lines;
  }, [lines]);
  const canResolve = mode === "sales" && !!priceBookId;

  const message = (key?: string) => (key ? t(key, { defaultValue: key }) : undefined);
  const cellError = (index: number, field: LineField) => message(errors[index]?.[field]);

  function patch(index: number, changes: Partial<LineDraft>) {
    onChange(lines.map((line, i) => (i === index ? { ...line, ...changes } : line)));
  }

  function move(index: number, delta: -1 | 1) {
    const target = index + delta;
    if (target < 0 || target >= lines.length) return;
    const next = [...lines];
    [next[index], next[target]] = [next[target] as LineDraft, next[index] as LineDraft];
    onChange(next);
  }

  function applyPrices(prices: Map<string, { unitPrice: number; source: LinePriceSource }>) {
    onChange(
      linesRef.current.map((line) => {
        const price = line.productId ? prices.get(line.productId) : undefined;
        return price ? { ...line, unitPrice: price.unitPrice, priceSource: price.source } : line;
      })
    );
  }

  async function resolveProducts(productIds: string[]) {
    if (!priceBookId || productIds.length === 0) return;
    try {
      const items = await resolvePrices(priceBookId, productIds);
      applyPrices(
        new Map(items.map((item) => [item.productId, { unitPrice: item.unitPrice, source: item.source }]))
      );
    } catch (error) {
      // pricebook.not_effective, forbidden, ...: the catalog price already in the row stays.
      toastApiError(error);
    }
  }

  function pickProduct(index: number, product: Product | null) {
    if (!product) {
      patch(index, { productId: undefined, productLabel: undefined, priceSource: undefined });
      return;
    }
    const purchase = mode === "purchase";
    patch(index, {
      productId: product.id,
      productLabel: product.name,
      description: product.name,
      // A purchase order starts from the vendor price; without one the price stays for the user to type.
      unitPrice: purchase ? (product.purchasePrice ?? lines[index]?.unitPrice ?? 0) : product.unitPrice,
      taxRate: product.taxRate,
      priceSource: purchase
        ? product.purchasePrice === undefined
          ? undefined
          : "purchase"
        : canResolve
          ? undefined
          : "catalog",
    });
    if (canResolve) void resolveProducts([product.id]);
  }

  async function repriceAll() {
    setRepricing("running");
    const ids = [...new Set(linesRef.current.flatMap((line) => (line.productId ? [line.productId] : [])))];
    await resolveProducts(ids);
    setRepricing("idle");
  }

  const label = (key: string, index: number) => `${t(`commerce:lines.${key}`)} ${index + 1}`;

  const numberProps = {
    hideControls: true,
    size: "sm",
    disabled,
    styles: { input: { textAlign: "right" as const } },
  };

  return (
    <Stack gap="md">
      <Table.ScrollContainer minWidth={1040}>
        <Table verticalSpacing="xs" withRowBorders>
          <Table.Thead>
            <Table.Tr>
              <Table.Th w={40}>#</Table.Th>
              {canPickProducts && <Table.Th w={220}>{t("commerce:lines.product")}</Table.Th>}
              <Table.Th miw={200}>{t("commerce:lines.description")}</Table.Th>
              <Table.Th w={100}>{t("commerce:lines.quantity")}</Table.Th>
              <Table.Th w={130}>{t("commerce:lines.unitPrice")}</Table.Th>
              <Table.Th w={90}>{t("commerce:lines.discountPercent")}</Table.Th>
              <Table.Th w={90}>{t("commerce:lines.taxRate")}</Table.Th>
              <Table.Th w={130} ta="right">
                {t("commerce:lines.lineTotal")}
              </Table.Th>
              <Table.Th w={110} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {lines.map((line, index) => (
              <Table.Tr key={line.key} data-testid="line-row">
                <Table.Td>{index + 1}</Table.Td>
                {canPickProducts && (
                  <Table.Td>
                    <ProductPicker
                      aria-label={label("product", index)}
                      size="sm"
                      value={line.productId ?? null}
                      selectedLabel={line.productLabel}
                      currency={currency}
                      onProductChange={(product) => pickProduct(index, product)}
                      disabled={disabled}
                      error={cellError(index, "productId")}
                    />
                  </Table.Td>
                )}
                <Table.Td>
                  <TextInput
                    aria-label={label("description", index)}
                    size="sm"
                    value={line.description}
                    onChange={(event) => patch(index, { description: event.currentTarget.value })}
                    disabled={disabled}
                    error={cellError(index, "description")}
                  />
                </Table.Td>
                <Table.Td>
                  <NumberInput
                    aria-label={label("quantity", index)}
                    {...numberProps}
                    value={line.quantity}
                    onChange={(value) => patch(index, { quantity: value })}
                    min={0}
                    decimalScale={4}
                    error={cellError(index, "quantity")}
                  />
                </Table.Td>
                <Table.Td>
                  <NumberInput
                    aria-label={label("unitPrice", index)}
                    {...numberProps}
                    value={line.unitPrice}
                    onChange={(value) => patch(index, { unitPrice: value, priceSource: undefined })}
                    min={0}
                    decimalScale={4}
                    error={cellError(index, "unitPrice")}
                  />
                  {line.priceSource && (
                    <Badge size="xs" variant="light" mt={4} data-testid="price-source">
                      {t(`commerce:lines.source.${line.priceSource}`)}
                    </Badge>
                  )}
                </Table.Td>
                <Table.Td>
                  <NumberInput
                    aria-label={label("discountPercent", index)}
                    {...numberProps}
                    value={line.discountPercent}
                    onChange={(value) => patch(index, { discountPercent: value })}
                    min={0}
                    max={100}
                    decimalScale={2}
                    error={cellError(index, "discountPercent")}
                  />
                </Table.Td>
                <Table.Td>
                  <NumberInput
                    aria-label={label("taxRate", index)}
                    {...numberProps}
                    value={line.taxRate}
                    onChange={(value) => patch(index, { taxRate: value })}
                    min={0}
                    max={100}
                    decimalScale={2}
                    error={cellError(index, "taxRate")}
                  />
                </Table.Td>
                <Table.Td ta="right">
                  <Text size="sm" fw={600} data-testid="line-total">
                    {formatMoney(totals.lines[index]?.lineTotal ?? 0, currency)}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Group gap={2} wrap="nowrap" justify="flex-end">
                    <Tooltip label={t("commerce:lines.moveUp")}>
                      <ActionIcon
                        variant="subtle"
                        aria-label={label("moveUp", index)}
                        disabled={disabled || index === 0}
                        onClick={() => move(index, -1)}
                      >
                        <ArrowUp size={16} />
                      </ActionIcon>
                    </Tooltip>
                    <Tooltip label={t("commerce:lines.moveDown")}>
                      <ActionIcon
                        variant="subtle"
                        aria-label={label("moveDown", index)}
                        disabled={disabled || index === lines.length - 1}
                        onClick={() => move(index, 1)}
                      >
                        <ArrowDown size={16} />
                      </ActionIcon>
                    </Tooltip>
                    <Tooltip label={t("commerce:lines.remove")}>
                      <ActionIcon
                        variant="subtle"
                        color="red"
                        aria-label={label("remove", index)}
                        disabled={disabled}
                        onClick={() => onChange(lines.filter((_, i) => i !== index))}
                      >
                        <Trash2 size={16} />
                      </ActionIcon>
                    </Tooltip>
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
            {lines.length === 0 && (
              <Table.Tr>
                <Table.Td colSpan={canPickProducts ? 9 : 8}>
                  <Text size="sm" c="dimmed" ta="center" py="md">
                    {t("commerce:lines.empty")}
                  </Text>
                </Table.Td>
              </Table.Tr>
            )}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>

      <Group justify="space-between" align="flex-start" wrap="wrap">
        <Group gap="sm">
          <Button
            variant="light"
            leftSection={<Plus size={16} />}
            onClick={() => onChange([...lines, newLine()])}
            disabled={disabled || lines.length >= MAX_LINES}
          >
            {t("commerce:lines.add")}
          </Button>
          {canResolve && (
            <Button
              variant="default"
              leftSection={<RefreshCw size={16} />}
              onClick={() => setRepricing("confirm")}
              disabled={disabled || !lines.some((line) => line.productId)}
            >
              {t("commerce:lines.reprice")}
            </Button>
          )}
        </Group>
        <TotalsCard
          totals={totals}
          currency={currency}
          preview
          adjustmentEditor={
            onAdjustmentChange ? (
              <AdjustmentField
                value={adjustment}
                onChange={onAdjustmentChange}
                onRound={() => onAdjustmentChange(roundingAdjustment(computeTotals(lines, 0).grandTotal))}
                issue={adjustmentIssue}
                serverError={adjustmentError}
                disabled={disabled}
                roundDisabled={lines.length === 0}
              />
            ) : undefined
          }
        />
      </Group>

      <ConfirmDialog
        opened={repricing !== "idle"}
        title={t("commerce:lines.repriceTitle")}
        message={t("commerce:lines.repriceMessage")}
        confirmLabel={t("commerce:lines.reprice")}
        loading={repricing === "running"}
        onConfirm={() => void repriceAll()}
        onClose={() => setRepricing("idle")}
      />
    </Stack>
  );
}
