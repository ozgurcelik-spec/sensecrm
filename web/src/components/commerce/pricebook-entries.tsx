import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useDebouncedValue } from "@mantine/hooks";
import { ActionIcon, Button, Group, NumberInput, Skeleton, Stack, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { Plus, Search, Trash2 } from "lucide-react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { LoadError } from "@/components/load-error";
import { LookupDialog } from "@/components/lookup/lookup-dialog";
import { useProductSource } from "@/components/lookup/lookup-sources";
import { useProductLookupCreate } from "@/components/lookup/lookup-create-product";
import { useDeletePriceBookEntry, usePriceBookEntries, useUpsertPriceBookEntry } from "@/hooks/use-pricebooks";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import { toScaledInt } from "@/lib/commerce-totals";
import { formatMoney } from "@/lib/format";
import type { PriceBook, PriceBookEntry } from "@/types";

const PAGE_SIZE = 25;

interface PriceBookEntriesProps {
  priceBook: PriceBook;
  canWrite: boolean;
}

function decimals(value: number): number {
  const text = value.toString();
  return text.includes("e") ? Number.POSITIVE_INFINITY : (text.split(".")[1]?.length ?? 0);
}

/** One row: the price is edited in place and saved when the field is left or Enter is pressed. */
function EntryRow({
  entry,
  priceBook,
  canWrite,
  onDelete,
}: {
  entry: PriceBookEntry;
  priceBook: PriceBook;
  canWrite: boolean;
  onDelete: (entry: PriceBookEntry) => void;
}) {
  const { t } = useTranslation(["inventory"]);
  const upsert = useUpsertPriceBookEntry();
  const [draft, setDraft] = useState<number | string>(entry.unitPrice);
  const [error, setError] = useState<string | undefined>();

  function save() {
    const value = typeof draft === "number" ? draft : draft.trim() === "" ? Number.NaN : Number(draft);
    if (!Number.isFinite(value) || value < 0) return setError(t("inventory:priceBooks.entries.priceInvalid"));
    if (value > 1_000_000_000 || decimals(value) > 4) return setError(t("inventory:priceBooks.entries.priceInvalid"));
    setError(undefined);
    if (toScaledInt(value, 4) === toScaledInt(entry.unitPrice, 4)) return;
    upsert.mutate(
      { id: priceBook.id, productId: entry.productId, unitPrice: value },
      {
        onSuccess: () => toast({ variant: "success", description: t("inventory:priceBooks.entries.saved") }),
        onError: (err) => {
          setDraft(entry.unitPrice);
          toastApiError(err);
        },
      }
    );
  }

  return (
    <Table.Tr data-testid="entry-row">
      <Table.Td>
        <Text size="sm">{entry.productName}</Text>
        {entry.productCode && (
          <Text size="xs" c="dimmed">
            {entry.productCode}
          </Text>
        )}
      </Table.Td>
      <Table.Td ta="right">{formatMoney(entry.catalogPrice, priceBook.currency)}</Table.Td>
      <Table.Td ta="right">
        {canWrite ? (
          <NumberInput
            aria-label={t("inventory:priceBooks.entries.priceFor", { name: entry.productName })}
            size="xs"
            w={140}
            ml="auto"
            hideControls
            min={0}
            decimalScale={4}
            value={draft}
            onChange={setDraft}
            onBlur={save}
            onKeyDown={(event) => {
              if (event.key === "Enter") {
                event.preventDefault();
                save();
              }
            }}
            disabled={upsert.isPending}
            error={error}
            styles={{ input: { textAlign: "right" } }}
          />
        ) : (
          formatMoney(entry.unitPrice, priceBook.currency)
        )}
      </Table.Td>
      <Table.Td w={50}>
        {canWrite && (
          <Tooltip label={t("inventory:priceBooks.entries.remove")}>
            <ActionIcon
              variant="subtle"
              color="red"
              aria-label={t("inventory:priceBooks.entries.removeFor", { name: entry.productName })}
              onClick={() => onDelete(entry)}
            >
              <Trash2 size={16} />
            </ActionIcon>
          </Tooltip>
        )}
      </Table.Td>
    </Table.Tr>
  );
}

/** Entry table of a `perProduct` price book: search, in-place price edit, "Ürün ekle" (product lookup window), remove. */
export function PriceBookEntries({ priceBook, canWrite }: PriceBookEntriesProps) {
  const { t } = useTranslation(["inventory"]);
  const [search, setSearch] = useState("");
  const [debounced] = useDebouncedValue(search.trim(), 250);
  const [page, setPage] = useState(1);
  const [adding, setAdding] = useState(false);
  const [removing, setRemoving] = useState<PriceBookEntry | null>(null);
  const productSource = useProductSource();
  const productCreate = useProductLookupCreate();
  const upsert = useUpsertPriceBookEntry();
  const remove = useDeletePriceBookEntry();
  const { data, isLoading, error, refetch } = usePriceBookEntries(priceBook.id, {
    page,
    pageSize: PAGE_SIZE,
    q: debounced || undefined,
  });

  const total = data?.totalCount ?? 0;
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  function addProduct(productId: string, catalogPrice: number) {
    // The product starts at its catalog price; the price is then edited in the table.
    upsert.mutate(
      { id: priceBook.id, productId, unitPrice: catalogPrice },
      {
        onSuccess: () => toast({ variant: "success", description: t("inventory:priceBooks.entries.added") }),
        onError: (err) => {
          const productError = getApiProblem(err)?.errors?.productId?.[0];
          if (productError) toast({ variant: "destructive", description: productError });
          else toastApiError(err);
        },
      }
    );
  }

  async function confirmRemove() {
    if (!removing) return;
    try {
      await remove.mutateAsync({ id: priceBook.id, productId: removing.productId });
      toast({ variant: "success", description: t("inventory:priceBooks.entries.removed") });
    } catch (err) {
      toastApiError(err);
    }
    setRemoving(null);
  }

  return (
    <Stack gap="md">
      <Group justify="space-between" wrap="wrap">
        <TextInput
          aria-label={t("inventory:priceBooks.entries.search")}
          placeholder={t("inventory:priceBooks.entries.search")}
          leftSection={<Search size={16} />}
          value={search}
          onChange={(event) => {
            setSearch(event.currentTarget.value);
            setPage(1);
          }}
          w={280}
        />
        {canWrite && (
          <Button leftSection={<Plus size={16} />} onClick={() => setAdding(true)}>
            {t("inventory:priceBooks.entries.add")}
          </Button>
        )}
      </Group>

      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading ? (
        <Skeleton h={80} />
      ) : (
        <Table.ScrollContainer minWidth={560}>
          <Table verticalSpacing="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("inventory:priceBooks.entries.product")}</Table.Th>
                <Table.Th ta="right">{t("inventory:priceBooks.entries.catalogPrice")}</Table.Th>
                <Table.Th ta="right">{t("inventory:priceBooks.entries.listPrice")}</Table.Th>
                <Table.Th w={50} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(data?.items ?? []).map((entry) => (
                // Keyed by the saved price too: a refetched value resets the draft.
                <EntryRow
                  key={`${entry.productId}:${entry.unitPrice}`}
                  entry={entry}
                  priceBook={priceBook}
                  canWrite={canWrite}
                  onDelete={setRemoving}
                />
              ))}
              {(data?.items.length ?? 0) === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={4}>
                    <Text size="sm" c="dimmed" ta="center" py="md">
                      {t("inventory:priceBooks.entries.empty")}
                    </Text>
                  </Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      {total > PAGE_SIZE && (
        <Group justify="flex-end" gap="xs">
          <Button variant="default" size="compact-sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>
            {t("inventory:lookup.previous")}
          </Button>
          <Text size="sm">{t("inventory:lookup.page", { page, pages })}</Text>
          <Button variant="default" size="compact-sm" disabled={page >= pages} onClick={() => setPage(page + 1)}>
            {t("inventory:lookup.next")}
          </Button>
        </Group>
      )}

      <LookupDialog
        opened={adding}
        onClose={() => setAdding(false)}
        title={t("inventory:priceBooks.entries.addTitle")}
        source={productSource}
        create={productCreate}
        filters={{ currency: priceBook.currency }}
        onSelect={(product) => addProduct(product.id, product.unitPrice)}
      />
      <ConfirmDialog
        opened={!!removing}
        title={t("inventory:priceBooks.entries.removeTitle")}
        message={t("inventory:priceBooks.entries.removeMessage", { name: removing?.productName })}
        confirmLabel={t("inventory:priceBooks.entries.remove")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmRemove()}
        onClose={() => setRemoving(null)}
      />
    </Stack>
  );
}
