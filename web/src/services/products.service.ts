/** Product catalog - `/products` (docs/plan/m6a-ticaret.md). */
import { apiClient } from "@/lib/api-client";
import type { ListResult, Product, ProductInput } from "@/types";
import { getList, getOne, seg, type ListQuery } from "./crm-http";

export interface ProductListQuery extends ListQuery {
  /** "true" / "false" (comes straight from the URL). */
  isActive?: string | boolean;
  currency?: string;
  /** M9C: products whose primary vendor is this one. */
  vendorId?: string;
}

export const productKeys = {
  all: ["products"] as const,
  list: (query: ProductListQuery) => ["products", "list", query] as const,
  detail: (id: string) => ["products", "detail", id] as const,
};

export const listProducts = (query: ProductListQuery): Promise<ListResult<Product>> =>
  getList<Product>("/products", query);

export const getProduct = (id: string): Promise<Product> =>
  getOne<Product>(`/products/${seg(id)}`);

export async function createProduct(input: ProductInput): Promise<Product> {
  const { data } = await apiClient.post<Product>("/products", input);
  return data;
}

export async function updateProduct(id: string, input: ProductInput): Promise<void> {
  await apiClient.put(`/products/${seg(id)}`, input);
}

export async function deleteProduct(id: string): Promise<void> {
  await apiClient.delete(`/products/${seg(id)}`);
}
