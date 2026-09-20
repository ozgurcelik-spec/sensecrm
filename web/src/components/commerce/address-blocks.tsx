import type { ChangeEvent, ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Button, Card, Group, Menu, SimpleGrid, Stack, Text, TextInput } from "@mantine/core";
import { ChevronDown, Copy, Eraser } from "lucide-react";
import { useAccount } from "@/hooks/use-accounts";
import {
  ADDRESS_FIELDS,
  ADDRESS_MAX,
  EMPTY_DOCUMENT_ADDRESS,
  copyAddress,
  fromAccountAddress,
  type AddressField,
  type DocumentAddressValues,
} from "@/lib/document-address";

export type AddressErrors = Partial<Record<AddressField, string>>;

interface AddressBlockProps {
  legend: string;
  /** Prefix of the accessible names, for example "billing" (so two blocks never share a label). */
  name: string;
  values: DocumentAddressValues;
  onChange: (values: DocumentAddressValues) => void;
  errors?: AddressErrors;
  disabled?: boolean;
  /** Extra action next to "Tümünü temizle" (for example the copy menu). */
  actions?: ReactNode;
}

/** One address block: country, building, street, city, state, postal code, with "Tümünü temizle". */
export function AddressBlock({ legend, name, values, onChange, errors = {}, disabled, actions }: AddressBlockProps) {
  const { t } = useTranslation(["commerce"]);
  const field = (key: AddressField) => ({
    label: t(`commerce:address.${key}`),
    "aria-label": `${legend} - ${t(`commerce:address.${key}`)}`,
    name: `${name}.${key}`,
    value: values[key],
    maxLength: ADDRESS_MAX[key] + 50,
    disabled,
    error: errors[key],
    onChange: (event: ChangeEvent<HTMLInputElement>) =>
      onChange({ ...values, [key]: event.currentTarget.value }),
  });
  return (
    <Card withBorder padding="md" data-testid={`address-${name}`}>
      <Stack gap="sm">
        <Group justify="space-between" wrap="nowrap">
          <Text fw={600}>{legend}</Text>
          <Group gap="xs" wrap="nowrap">
            {actions}
            <Button
              variant="subtle"
              size="compact-sm"
              color="gray"
              leftSection={<Eraser size={14} />}
              disabled={disabled}
              aria-label={`${legend} - ${t("commerce:address.clearAll")}`}
              onClick={() => onChange({ ...EMPTY_DOCUMENT_ADDRESS })}
            >
              {t("commerce:address.clearAll")}
            </Button>
          </Group>
        </Group>
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
          {ADDRESS_FIELDS.map((key) => (
            <TextInput key={key} {...field(key)} />
          ))}
        </SimpleGrid>
      </Stack>
    </Card>
  );
}

interface AddressBlocksProps {
  billing: DocumentAddressValues;
  shipping: DocumentAddressValues;
  onBillingChange: (values: DocumentAddressValues) => void;
  onShippingChange: (values: DocumentAddressValues) => void;
  billingErrors?: AddressErrors;
  shippingErrors?: AddressErrors;
  /** The document's account: "Firmadan getir" copies its billing address (needs `crm.accounts.read`). */
  accountId?: string;
  canReadAccounts?: boolean;
  disabled?: boolean;
}

/**
 * "Adres Bilgileri": billing and shipping ("İletişim") blocks with the client-only "Adres Kopyala"
 * menu: billing to shipping, shipping to billing, and the account's address into the billing block.
 * The server stores both blocks independently (no copy rule).
 */
export function AddressBlocks({
  billing,
  shipping,
  onBillingChange,
  onShippingChange,
  billingErrors,
  shippingErrors,
  accountId,
  canReadAccounts = false,
  disabled,
}: AddressBlocksProps) {
  const { t } = useTranslation(["commerce"]);
  const account = useAccount(canReadAccounts && accountId ? accountId : undefined);
  const canFetch = canReadAccounts && !!accountId;

  const copyMenu = (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <Button
          variant="light"
          size="compact-sm"
          leftSection={<Copy size={14} />}
          rightSection={<ChevronDown size={14} />}
          disabled={disabled}
        >
          {t("commerce:address.copy")}
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item onClick={() => onShippingChange(copyAddress(billing))}>
          {t("commerce:address.billingToShipping")}
        </Menu.Item>
        <Menu.Item onClick={() => onBillingChange(copyAddress(shipping))}>
          {t("commerce:address.shippingToBilling")}
        </Menu.Item>
        {canFetch && (
          <Menu.Item
            disabled={!account.data}
            onClick={() => account.data && onBillingChange(fromAccountAddress(account.data.billingAddress))}
          >
            {t("commerce:address.fromAccount")}
          </Menu.Item>
        )}
      </Menu.Dropdown>
    </Menu>
  );

  return (
    <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
      <AddressBlock
        name="billing"
        legend={t("commerce:address.billing")}
        values={billing}
        onChange={onBillingChange}
        errors={billingErrors}
        disabled={disabled}
        actions={copyMenu}
      />
      <AddressBlock
        name="shipping"
        legend={t("commerce:address.shipping")}
        values={shipping}
        onChange={onShippingChange}
        errors={shippingErrors}
        disabled={disabled}
      />
    </SimpleGrid>
  );
}
