import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useDebouncedValue } from "@mantine/hooks";
import { Select, type SelectProps } from "@mantine/core";
import { useProducts } from "@/hooks/use-products";
import type { Product } from "@/types";

type ProductPickerProps = Omit<
  SelectProps,
  "data" | "searchValue" | "onSearchChange" | "value" | "onChange"
> & {
  value: string | null;
  /** Name shown for `value` before / without a search (the selected product may not be in the list). */
  selectedLabel?: string;
  /** Document currency: products in another currency are listed but disabled (no conversion). */
  currency: string;
  onProductChange: (product: Product | null) => void;
};

/**
 * Product picker with server-side search of the active catalog (`GET /products?isActive=true&q=`).
 * Needs `crm.products.read`; the caller decides whether to render it.
 */
export function ProductPicker({
  value,
  selectedLabel,
  currency,
  onProductChange,
  ...props
}: ProductPickerProps) {
  const { t } = useTranslation(["commerce"]);
  const [search, setSearch] = useState("");
  const [debounced] = useDebouncedValue(search.trim(), 250);
  const { data, isFetching } = useProducts({
    page: 1,
    pageSize: 20,
    isActive: true,
    q: debounced || undefined,
  });

  const options = useMemo(() => {
    const list = (data?.items ?? []).map((product) => {
      const mismatch = product.currency !== currency;
      const name = product.code ? `${product.name} (${product.code})` : product.name;
      return {
        value: product.id,
        label: mismatch ? `${name} - ${product.currency}` : name,
        disabled: mismatch,
      };
    });
    if (value && !list.some((option) => option.value === value)) {
      list.unshift({ value, label: selectedLabel ?? value, disabled: false });
    }
    return list;
  }, [data, currency, value, selectedLabel]);

  return (
    <Select
      placeholder={t("commerce:lines.productPlaceholder")}
      nothingFoundMessage={isFetching ? t("commerce:lines.searching") : t("commerce:lines.noProducts")}
      data={options}
      value={value}
      onChange={(next) => {
        if (!next) return onProductChange(null);
        // The current value may be an option that is not in the fetched page: nothing to apply then.
        const product = data?.items.find((p) => p.id === next);
        if (product) onProductChange(product);
      }}
      searchable
      clearable
      filter={({ options: all }) => all}
      searchValue={search}
      onSearchChange={setSearch}
      {...props}
    />
  );
}
