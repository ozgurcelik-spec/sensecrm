import { useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Badge, Button, Card, Group, Text } from "@mantine/core";
import { LookupDialog } from "@/components/lookup/lookup-dialog";
import { usePriceBookLookupCreate } from "@/components/lookup/lookup-creates";
import { usePriceBookSource } from "@/components/lookup/lookup-sources";
import { useAccountDefaultPriceBook, useSetAccountDefaultPriceBook } from "@/hooks/use-pricebooks";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { PERMISSIONS } from "@/types";

/**
 * "Varsayılan fiyat listesi" row of an account page. Needs `crm.pricebooks.read`; changing it needs
 * `crm.pricebooks.write`. The server never applies it by itself: the document editors only suggest it.
 */
export function AccountPriceBook({ accountId }: { accountId: string }) {
  const { t } = useTranslation(["inventory"]);
  const canRead = usePermission(PERMISSIONS.crmPriceBooksRead);
  const canWrite = usePermission(PERMISSIONS.crmPriceBooksWrite);
  const { data, isLoading } = useAccountDefaultPriceBook(accountId, canRead);
  const set = useSetAccountDefaultPriceBook();
  const source = usePriceBookSource();
  const create = usePriceBookLookupCreate();
  const [picking, setPicking] = useState(false);

  if (!canRead) return null;

  function change(priceBookId: string | null) {
    set.mutate(
      { accountId, priceBookId },
      {
        onSuccess: () => toast({ variant: "success", description: t("inventory:accountDefault.saved") }),
        onError: (error) => toastApiError(error),
      }
    );
  }

  return (
    <Card withBorder padding="md" data-testid="account-price-book">
      <Group justify="space-between" wrap="wrap">
        <div>
          <Text size="xs" c="dimmed">
            {t("inventory:accountDefault.label")}
          </Text>
          {isLoading ? (
            <Text size="sm">...</Text>
          ) : data ? (
            <Group gap="xs">
              <Anchor component={Link} to={`/app/pricebooks/${data.priceBookId}`} size="sm">
                {data.priceBookName}
              </Anchor>
              {!data.isEffective && (
                <Badge variant="light" color="orange" size="sm">
                  {t("inventory:accountDefault.notEffective")}
                </Badge>
              )}
            </Group>
          ) : (
            <Text size="sm">{t("inventory:accountDefault.none")}</Text>
          )}
        </div>
        {canWrite && (
          <Group gap="xs">
            <Button variant="default" size="compact-sm" loading={set.isPending} onClick={() => setPicking(true)}>
              {t("inventory:accountDefault.change")}
            </Button>
            {data && (
              <Button variant="subtle" color="red" size="compact-sm" disabled={set.isPending} onClick={() => change(null)}>
                {t("inventory:accountDefault.clear")}
              </Button>
            )}
          </Group>
        )}
      </Group>
      <LookupDialog
        opened={picking}
        onClose={() => setPicking(false)}
        title={t("inventory:priceBooks.select")}
        source={source}
        create={create}
        selectedId={data?.priceBookId}
        filters={{ effective: true }}
        onSelect={(book) => change(book.id)}
      />
    </Card>
  );
}
