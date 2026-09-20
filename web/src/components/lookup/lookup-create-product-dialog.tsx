/** Product quick-create dialog of the lookup window (kept apart: the product form itself uses the vendor lookup). */
import { ProductFormDialog } from "@/components/commerce/product-form-dialog";
import { toastApiError } from "@/hooks/use-toast";
import { getProduct } from "@/services/products.service";
import type { Product } from "@/types";
import type { LookupCreateDialogProps } from "./lookup-types";

export function ProductCreateDialog({ opened, onClose, initialName, onCreated }: LookupCreateDialogProps<Product>) {
  if (!opened) return null;
  return (
    <ProductFormDialog
      initialName={initialName}
      onClose={onClose}
      onSaved={(id) => {
        getProduct(id)
          .then(onCreated)
          .catch((error: unknown) => toastApiError(error));
      }}
    />
  );
}
