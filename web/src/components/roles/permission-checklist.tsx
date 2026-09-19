import { useTranslation } from "react-i18next";
import { Checkbox, Fieldset, SimpleGrid, Stack } from "@mantine/core";
import type { Permission, PermissionGroup } from "@/types";

const GROUP_ORDER: PermissionGroup[] = ["org", "crm"];

interface PermissionChecklistProps {
  catalog: Permission[];
  value: string[];
  onChange: (value: string[]) => void;
  readOnly?: boolean;
}

/** Permission checkboxes grouped like Zoho profiles (organization administration / CRM modules). */
export function PermissionChecklist({
  catalog,
  value,
  onChange,
  readOnly = false,
}: PermissionChecklistProps) {
  const { t } = useTranslation(["users"]);
  const selected = new Set(value);

  function toggle(keys: string[], checked: boolean) {
    const next = new Set(selected);
    keys.forEach((key) => (checked ? next.add(key) : next.delete(key)));
    onChange(catalog.map((p) => p.key).filter((key) => next.has(key)));
  }

  return (
    <Stack gap="md">
      {GROUP_ORDER.map((group) => {
        const items = catalog.filter((p) => p.group === group);
        if (items.length === 0) return null;
        const keys = items.map((p) => p.key);
        const count = keys.filter((key) => selected.has(key)).length;
        return (
          <Fieldset
            key={group}
            legend={
              <Checkbox
                label={t(`users:roles.groups.${group}`)}
                checked={count === keys.length}
                indeterminate={count > 0 && count < keys.length}
                onChange={(event) => toggle(keys, event.currentTarget.checked)}
                disabled={readOnly}
                fw={600}
              />
            }
          >
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="xs">
              {items.map((permission) => (
                <Checkbox
                  key={permission.key}
                  label={t(`users:permissions.${permission.key}`, { defaultValue: permission.key })}
                  checked={selected.has(permission.key)}
                  onChange={(event) => toggle([permission.key], event.currentTarget.checked)}
                  disabled={readOnly}
                />
              ))}
            </SimpleGrid>
          </Fieldset>
        );
      })}
    </Stack>
  );
}
