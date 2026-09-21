import { useTranslation } from "react-i18next";
import { Button, Checkbox, Fieldset, Group, Input, SimpleGrid, Stack, Tooltip } from "@mantine/core";
import { groupScopes, isReadScope } from "@/lib/integrations";

interface ScopePickerProps {
  /** Every scope that may be listed (`crm.*` minus `crm.approvals.decide`). */
  options: readonly string[];
  /** The scopes the creator may hand out (their own effective permissions); the others are dimmed and locked. */
  owned: ReadonlySet<string>;
  value: string[];
  onChange: (value: string[]) => void;
  error?: string;
  disabled?: boolean;
}

/**
 * Scope checkboxes of an API key grouped by resource, with "all / none" per group and a read-only
 * preset. A key can never hold more than its creator, so scopes the creator lacks cannot be ticked
 * (the server refuses them with `role.permission_escalation` anyway). `org.*` and approval decisions
 * never appear.
 */
export function ScopePicker({ options, owned, value, onChange, error, disabled = false }: ScopePickerProps) {
  const { t } = useTranslation(["integrations", "users"]);
  const selected = new Set(value);
  const order = [...options];

  function commit(next: Set<string>) {
    onChange(order.filter((key) => next.has(key)));
  }

  function toggle(keys: readonly string[], checked: boolean) {
    const next = new Set(selected);
    for (const key of keys) {
      if (!owned.has(key)) continue;
      if (checked) next.add(key);
      else next.delete(key);
    }
    commit(next);
  }

  const ownedOptions = order.filter((key) => owned.has(key));
  return (
    <Input.Wrapper
      label={t("integrations:apiKeys.form.scopes")}
      description={t("integrations:apiKeys.form.scopesHint")}
      withAsterisk
      error={error}
    >
      <Stack gap="sm" mt="xs" data-testid="scope-picker">
        <Group gap="xs">
          <Button
            size="compact-xs"
            variant="light"
            disabled={disabled}
            onClick={() => commit(new Set(ownedOptions.filter(isReadScope)))}
          >
            {t("integrations:apiKeys.form.presetRead")}
          </Button>
          <Button size="compact-xs" variant="light" disabled={disabled} onClick={() => commit(new Set(ownedOptions))}>
            {t("integrations:apiKeys.form.presetAll")}
          </Button>
          <Button size="compact-xs" variant="subtle" color="gray" disabled={disabled} onClick={() => commit(new Set())}>
            {t("integrations:apiKeys.form.presetNone")}
          </Button>
        </Group>
        {groupScopes(order).map((group) => {
          const grantable = group.scopes.filter((key) => owned.has(key));
          const count = grantable.filter((key) => selected.has(key)).length;
          const label = t(`integrations:apiKeys.scopeGroups.${group.resource}`, {
            defaultValue: group.resource.charAt(0).toUpperCase() + group.resource.slice(1),
          });
          return (
            <Fieldset
              key={group.resource}
              legend={
                <Checkbox
                  label={label}
                  aria-label={t("integrations:apiKeys.form.groupAll", { group: label })}
                  checked={grantable.length > 0 && count === grantable.length}
                  indeterminate={count > 0 && count < grantable.length}
                  disabled={disabled || grantable.length === 0}
                  onChange={(event) => toggle(grantable, event.currentTarget.checked)}
                  fw={600}
                />
              }
            >
              <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="xs">
                {group.scopes.map((key) => {
                  const locked = !owned.has(key);
                  return (
                    <Tooltip key={key} label={t("integrations:apiKeys.form.notOwned")} disabled={!locked}>
                      <div>
                        <Checkbox
                          label={t(`users:permissions.${key}`, { defaultValue: key })}
                          description={key}
                          checked={selected.has(key)}
                          disabled={disabled || locked}
                          onChange={(event) => toggle([key], event.currentTarget.checked)}
                          data-testid={`scope-${key}`}
                        />
                      </div>
                    </Tooltip>
                  );
                })}
              </SimpleGrid>
            </Fieldset>
          );
        })}
      </Stack>
    </Input.Wrapper>
  );
}
