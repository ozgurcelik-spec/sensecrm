import { useState } from "react";
import { useTranslation } from "react-i18next";
import { NumberInput, Select, SimpleGrid, TextInput } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";
import { useRecordInvoicePayment } from "@/hooks/use-invoices";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import { toSignedMinor } from "@/lib/commerce-totals";
import { blankToUndefined, formatMoney } from "@/lib/format";
import { PAYMENT_METHODS, type Invoice, type InvoicePaymentMethod } from "@/types";

interface PaymentDialogProps {
  invoice: Pick<Invoice, "id" | "currency" | "balanceAmount" | "invoiceDate">;
  /** Today in the organization's calendar (`YYYY-MM-DD`): the latest allowed payment date. */
  today: string;
  onClose: () => void;
}

type FieldErrors = Partial<Record<"amount" | "paidOn" | "reference" | "notes", string>>;

/**
 * "Ödeme kaydet": amount (at most the balance), date (not in the future, not before the invoice date),
 * method, reference and notes. `invoice.payment_exceeds_balance` and the date rules of the server land
 * on their fields; `commerce.concurrent_update` / `invoice.not_payable` are toasted and the invoice is
 * refetched by the mutation. Mount only while open.
 */
export function PaymentDialog({ invoice, today, onClose }: PaymentDialogProps) {
  const { t } = useTranslation(["invoices", "common"]);
  const record = useRecordInvoicePayment();
  const invoiceDate = invoice.invoiceDate.slice(0, 10);
  const [amount, setAmount] = useState<number | string>(invoice.balanceAmount);
  const [paidOn, setPaidOn] = useState(today);
  const [method, setMethod] = useState<InvoicePaymentMethod | null>(null);
  const [reference, setReference] = useState("");
  const [notes, setNotes] = useState("");
  const [errors, setErrors] = useState<FieldErrors>({});

  function check(): FieldErrors {
    const found: FieldErrors = {};
    const minor = toSignedMinor(amount);
    const balance = BigInt(Math.round(invoice.balanceAmount * 100));
    if (minor === null) found.amount = t("invoices:payment.validation.decimals");
    else if (minor <= 0n) found.amount = t("invoices:payment.validation.amountPositive");
    else if (minor > balance)
      found.amount = t("invoices:payment.validation.exceedsBalance", {
        balance: formatMoney(invoice.balanceAmount, invoice.currency),
      });
    if (!paidOn) found.paidOn = t("invoices:payment.validation.dateRequired");
    else if (paidOn > today) found.paidOn = t("invoices:payment.validation.future");
    else if (paidOn < invoiceDate) found.paidOn = t("invoices:payment.validation.beforeInvoice");
    if (reference.trim().length > 100) found.reference = t("invoices:payment.validation.referenceMax");
    if (notes.trim().length > 500) found.notes = t("invoices:payment.validation.notesMax");
    return found;
  }

  function submit() {
    const found = check();
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    record.mutate(
      {
        id: invoice.id,
        amount: Number(toSignedMinor(amount) ?? 0n) / 100,
        paidOn,
        method: method ?? undefined,
        reference: blankToUndefined(reference),
        notes: blankToUndefined(notes),
      },
      {
        onSuccess: () => {
          toast({ variant: "success", description: t("invoices:payment.recorded") });
          onClose();
        },
        onError: (error) => {
          const problem = getApiProblem(error);
          if (problem?.code === "invoice.payment_exceeds_balance") {
            const balance = Number(problem.args?.balance);
            setErrors({
              amount: t("invoices:payment.validation.exceedsBalance", {
                balance: formatMoney(Number.isFinite(balance) ? balance : invoice.balanceAmount, invoice.currency),
              }),
            });
            return;
          }
          const fromServer = problem?.errors;
          if (fromServer) {
            const mapped: FieldErrors = {};
            for (const [key, messages] of Object.entries(fromServer)) {
              const field = key.charAt(0).toLowerCase() + key.slice(1);
              if ((field === "amount" || field === "paidOn" || field === "reference" || field === "notes") && messages[0]) {
                mapped[field] = messages[0];
              }
            }
            if (Object.keys(mapped).length > 0) {
              setErrors(mapped);
              return;
            }
          }
          // invoice.not_payable, commerce.concurrent_update, ...: the invoice was refetched, tell the user.
          onClose();
          toastApiError(error);
        },
      }
    );
  }

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={t("invoices:payment.title")}
      size="md"
      loading={record.isPending}
      submitLabel={t("invoices:payment.submit")}
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <NumberInput
        label={t("invoices:payment.amount")}
        description={t("invoices:payment.balanceHint", {
          balance: formatMoney(invoice.balanceAmount, invoice.currency),
        })}
        withAsterisk
        data-autofocus
        hideControls
        min={0}
        decimalScale={2}
        value={amount}
        onChange={setAmount}
        error={errors.amount}
      />
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <TextInput
          label={t("invoices:payment.paidOn")}
          type="date"
          withAsterisk
          min={invoiceDate}
          max={today}
          value={paidOn}
          onChange={(event) => setPaidOn(event.currentTarget.value)}
          error={errors.paidOn}
        />
        <Select
          label={t("invoices:payment.method")}
          data={PAYMENT_METHODS.map((m) => ({ value: m, label: t(`invoices:payment.methods.${m}`) }))}
          value={method}
          onChange={(value) => setMethod(value as InvoicePaymentMethod | null)}
          clearable
        />
      </SimpleGrid>
      <TextInput
        label={t("invoices:payment.reference")}
        value={reference}
        onChange={(event) => setReference(event.currentTarget.value)}
        error={errors.reference}
      />
      <TextInput
        label={t("invoices:payment.notes")}
        value={notes}
        onChange={(event) => setNotes(event.currentTarget.value)}
        error={errors.notes}
      />
    </FormDialog>
  );
}
