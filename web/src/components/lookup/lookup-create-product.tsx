/** Product quick-create definition of the lookup window. */
import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { PERMISSIONS, type Product } from "@/types";
import { ProductCreateDialog } from "./lookup-create-product-dialog";
import type { LookupCreate } from "./lookup-types";

export function useProductLookupCreate(): LookupCreate<Product> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({
      permission: PERMISSIONS.crmProductsWrite,
      label: t("inventory:lookup.newProduct"),
      Dialog: ProductCreateDialog,
    }),
    [t]
  );
}
