import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Button } from "@mantine/core";
import { FilePlus2 } from "lucide-react";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";

interface CreateQuoteButtonProps {
  accountId: string;
  contactId?: string;
  dealId?: string;
}

/**
 * "Teklif oluştur" action of a deal / account page (`crm.quotes.write`). Opens the quote editor; only ids
 * travel in the URL, the editor looks up names, currency and (from a deal) the subject itself.
 */
export function CreateQuoteButton({ accountId, contactId, dealId }: CreateQuoteButtonProps) {
  const { t } = useTranslation(["commerce"]);
  const { canWriteQuotes } = useCrmPermissions();
  if (!canWriteQuotes) return null;

  const params = new URLSearchParams({ accountId });
  if (contactId) params.set("contactId", contactId);
  if (dealId) params.set("dealId", dealId);

  return (
    <Button
      component={Link}
      to={`/app/quotes/new?${params.toString()}`}
      variant="default"
      leftSection={<FilePlus2 size={16} />}
    >
      {t("commerce:quotes.create")}
    </Button>
  );
}
