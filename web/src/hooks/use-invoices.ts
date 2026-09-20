import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  convertOrderToInvoice,
  createInvoice,
  deleteInvoice,
  deleteInvoicePayment,
  getInvoice,
  invoiceKeys,
  listInvoices,
  recordInvoicePayment,
  runInvoiceAction,
  updateInvoice,
  type InvoiceAction,
  type InvoiceListQuery,
} from "@/services/invoices.service";
import type { InvoiceConvertInput, InvoiceInput, InvoicePaymentInput } from "@/types";

export function useInvoices(query: InvoiceListQuery, enabled = true) {
  return useQuery({
    queryKey: invoiceKeys.list(query),
    queryFn: () => listInvoices(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useInvoice(id: string | undefined) {
  return useQuery({
    queryKey: invoiceKeys.detail(id ?? ""),
    queryFn: () => getInvoice(id as string),
    enabled: !!id,
  });
}

/** Everything an invoice change can touch: invoice lists, the source order's invoice link, report, audit. */
function useInvalidateInvoices() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: invoiceKeys.all }),
      queryClient.invalidateQueries({ queryKey: ["orders"] }),
      queryClient.invalidateQueries({ queryKey: ["audit"] }),
      queryClient.invalidateQueries({ queryKey: ["reports"] }),
    ]);
}

export function useSaveInvoice() {
  const invalidate = useInvalidateInvoices();
  return useMutation({
    mutationFn: async ({ id, ...input }: InvoiceInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateInvoice(id, input);
        return id;
      }
      return (await createInvoice(input)).id;
    },
    onSuccess: invalidate,
  });
}

export function useDeleteInvoice() {
  const invalidate = useInvalidateInvoices();
  return useMutation({ mutationFn: (id: string) => deleteInvoice(id), onSuccess: invalidate });
}

interface InvoiceActionVariables {
  id: string;
  action: InvoiceAction;
  reason?: string;
}

/** send / revert / cancel. Refetches on failure as well: a 409 usually means the invoice changed under the user. */
export function useInvoiceAction() {
  const invalidate = useInvalidateInvoices();
  return useMutation({
    mutationFn: ({ id, action, reason }: InvoiceActionVariables) =>
      runInvoiceAction(id, action, action === "cancel" ? { reason } : undefined),
    onSettled: () => invalidate(),
  });
}

/** Payment ledger writes (`invoice.payment_exceeds_balance`, `commerce.concurrent_update` refetch the invoice too). */
export function useRecordInvoicePayment() {
  const invalidate = useInvalidateInvoices();
  return useMutation({
    mutationFn: ({ id, ...input }: InvoicePaymentInput & { id: string }) =>
      recordInvoicePayment(id, input),
    onSettled: () => invalidate(),
  });
}

export function useDeleteInvoicePayment() {
  const invalidate = useInvalidateInvoices();
  return useMutation({
    mutationFn: ({ id, paymentId }: { id: string; paymentId: string }) =>
      deleteInvoicePayment(id, paymentId),
    onSettled: () => invalidate(),
  });
}

/** `POST /orders/{id}/invoice`: refetches orders (their `invoiceId`) and invoices, on failure too. */
export function useConvertOrderToInvoice() {
  const invalidate = useInvalidateInvoices();
  return useMutation({
    mutationFn: ({ orderId, ...input }: InvoiceConvertInput & { orderId: string }) =>
      convertOrderToInvoice(orderId, input),
    onSettled: () => invalidate(),
  });
}
