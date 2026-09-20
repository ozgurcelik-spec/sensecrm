import { useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { ActionIcon, Group, TextInput } from "@mantine/core";
import { Search, X } from "lucide-react";
import { LookupDialog } from "./lookup-dialog";
import type { LookupDialogProps, LookupValue } from "./lookup-types";

export interface LookupFieldProps<T>
  extends Omit<LookupDialogProps<T>, "opened" | "onClose" | "onSelect" | "selectedId"> {
  label: string;
  /** The picked record; its label shows even when it is not in the searched list. */
  value: LookupValue | null;
  /** Called with the picked row (and its id / label), or `null` when cleared. */
  onChange(row: T | null, picked: LookupValue | null): void;
  placeholder?: string;
  description?: string;
  clearable?: boolean;
  required?: boolean;
  error?: string;
  disabled?: boolean;
  /** No read permission: the pre-filled value shows, but nothing can be searched or changed. */
  readOnly?: boolean;
}

/**
 * Read-only looking input (label + search icon) that opens the `LookupDialog`: click, Enter or Space
 * open it, the clear button empties it. Typeahead `Select`s (for example `AccountPicker`) stay
 * where they are; this is the editor field of the M9C plan.
 */
export function LookupField<T>({
  label,
  value,
  onChange,
  placeholder,
  description,
  clearable = false,
  required = false,
  error,
  disabled = false,
  readOnly = false,
  source,
  ...dialog
}: LookupFieldProps<T>) {
  const { t } = useTranslation(["inventory"]);
  const [opened, setOpened] = useState(false);
  const interactive = !disabled && !readOnly;

  function open() {
    if (interactive) setOpened(true);
  }

  function handleKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      open();
    }
  }

  return (
    <>
      <TextInput
        label={label}
        description={description}
        withAsterisk={required}
        error={error}
        readOnly
        disabled={disabled}
        aria-haspopup={interactive ? "dialog" : undefined}
        placeholder={placeholder ?? t("inventory:lookup.placeholder")}
        value={value?.label ?? ""}
        onClick={open}
        onKeyDown={handleKeyDown}
        styles={{ input: { cursor: interactive ? "pointer" : "default" } }}
        rightSectionWidth={clearable && value && interactive ? 56 : 32}
        rightSection={
          interactive ? (
            <Group gap={2} wrap="nowrap">
              {clearable && value && (
                <ActionIcon
                  variant="subtle"
                  size="sm"
                  color="gray"
                  aria-label={t("inventory:lookup.clear", { label })}
                  onClick={(event) => {
                    event.stopPropagation();
                    onChange(null, null);
                  }}
                >
                  <X size={14} />
                </ActionIcon>
              )}
              <ActionIcon
                variant="subtle"
                size="sm"
                color="gray"
                aria-label={t("inventory:lookup.open", { label })}
                onClick={open}
              >
                <Search size={14} />
              </ActionIcon>
            </Group>
          ) : undefined
        }
      />
      {opened && (
        <LookupDialog<T>
          {...dialog}
          source={source}
          opened
          onClose={() => setOpened(false)}
          selectedId={value?.id}
          onSelect={(row) => onChange(row, { id: source.getId(row), label: source.getLabel(row) })}
        />
      )}
    </>
  );
}
