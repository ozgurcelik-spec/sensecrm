import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, SimpleGrid, TextInput } from "@mantine/core";
import { FileText } from "lucide-react";
import { FormDialog } from "@/components/crm/form-dialog";
import { usePermission } from "@/hooks/use-permission";
import { useConvertOrderToInvoice } from "@/hooks/use-invoices";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import { orderInvoiceAction } from "@/lib/commerce-actions";
import { blankToUndefined } from "@/lib/format";
import { PERMISSIONS, type OrderStatus } from "@/types";

interface CreateInvoiceButtonProps {
  order: { id: string; status: OrderStatus; invoiceId?: string; invoiceNumber?: string };
}

function ConvertDialog({ orderId, onClose }: { orderId: string; onClose: () => void }) {
  const { t } = useTranslation(["invoices"]);
  const navigate = useNavigate();
  const convert = useConvertOrderToInvoice();
  const [invoiceDate, setInvoiceDate] = useState("");
  const [dueDate, setDueDate] = useState("");
  const [errors, setErrors] = useState<{ invoiceDate?: string; dueDate?: string }>({});

  function submit() {
    if (invoiceDate && dueDate && dueDate < invoiceDate) {
      setErrors({ dueDate: t("invoices:convert.dueBefore") });
      return;
    }
    setErrors({});
    convert.mutate(
      { orderId, invoiceDate: blankToUndefined(invoiceDate), dueDate: blankToUndefined(dueDate) },
      {
        onSuccess: (invoice) => {
          toast({ variant: "success", description: t("invoices:convert.created", { number: invoice.number }) });
          onClose();
          navigate(`/app/invoices/${invoice.id}`);
        },
        onError: (error) => {
          const fromServer = getApiProblem(error)?.errors;
          if (fromServer) {
            const mapped: { invoiceDate?: string; dueDate?: string } = {};
            for (const [key, messages] of Object.entries(fromServer)) {
              const field = key.charAt(0).toLowerCase() + key.slice(1);
              if ((field === "invoiceDate" || field === "dueDate") && messages[0]) mapped[field] = messages[0];
            }
            if (Object.keys(mapped).length > 0) {
              setErrors(mapped);
              return;
            }
          }
          // order.not_invoiceable / order.already_invoiced / commerce.related_not_found ...
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
      title={t("invoices:convert.title")}
      size="md"
      loading={convert.isPending}
      submitLabel={t("invoices:convert.submit")}
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <TextInput
          label={t("invoices:fields.invoiceDate")}
          description={t("invoices:convert.invoiceDateHint")}
          type="date"
          data-autofocus
          value={invoiceDate}
          onChange={(event) => setInvoiceDate(event.currentTarget.value)}
          error={errors.invoiceDate}
        />
        <TextInput
          label={t("invoices:fields.dueDate")}
          type="date"
          value={dueDate}
          onChange={(event) => setDueDate(event.currentTarget.value)}
          error={errors.dueDate}
        />
      </SimpleGrid>
    </FormDialog>
  );
}

/**
 * "Fatura oluştur" of an order (M9C): for a confirmed / fulfilled order without an active invoice and
 * with `crm.invoices.write`; an order that already has one shows the invoice link instead.
 */
export function CreateInvoiceButton({ order }: CreateInvoiceButtonProps) {
  const { t } = useTranslation(["invoices"]);
  const canWriteInvoices = usePermission(PERMISSIONS.crmInvoicesWrite);
  const canReadInvoices = usePermission(PERMISSIONS.crmInvoicesRead);
  const [open, setOpen] = useState(false);
  const action = orderInvoiceAction(order, { canWriteInvoices, canReadInvoices });

  if (action === "link") {
    return (
      <Anchor component={Link} to={`/app/invoices/${order.invoiceId}`} size="sm">
        {t("invoices:orderLink", { number: order.invoiceNumber ?? order.invoiceId })}
      </Anchor>
    );
  }
  if (action !== "create") return null;
  return (
    <>
      <Button leftSection={<FileText size={16} />} onClick={() => setOpen(true)}>
        {t("invoices:actions.createInvoice")}
      </Button>
      {open && <ConvertDialog orderId={order.id} onClose={() => setOpen(false)} />}
    </>
  );
}
