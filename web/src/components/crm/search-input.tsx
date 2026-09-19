import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { CloseButton, TextInput } from "@mantine/core";
import { Search } from "lucide-react";

interface SearchInputProps {
  /** Committed value (from the URL). */
  value: string;
  onSearch: (value: string) => void;
  placeholder?: string;
  delay?: number;
}

/**
 * Search box that keeps typing local and commits `onSearch` after the user pauses (debounce), so the
 * server is queried once per pause rather than per keystroke. Follows external changes to `value`.
 */
export function SearchInput({ value, onSearch, placeholder, delay = 300 }: SearchInputProps) {
  const { t } = useTranslation(["common"]);
  const [text, setText] = useState(value);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const committed = useRef(value);

  // Follow URL-driven changes (back button, "clear filters"); ignore echoes of our own commit.
  useEffect(() => {
    if (value !== committed.current) {
      committed.current = value;
      setText(value);
    }
  }, [value]);

  useEffect(() => () => clearTimeout(timer.current), []);

  function change(next: string) {
    setText(next);
    clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      const trimmed = next.trim();
      if (trimmed !== committed.current) {
        committed.current = trimmed;
        onSearch(trimmed);
      }
    }, delay);
  }

  function clear() {
    clearTimeout(timer.current);
    setText("");
    committed.current = "";
    onSearch("");
  }

  return (
    <TextInput
      type="search"
      role="searchbox"
      leftSection={<Search size={16} />}
      rightSection={
        text ? <CloseButton size="sm" aria-label={t("common:clear")} onClick={clear} /> : undefined
      }
      placeholder={placeholder ?? t("common:search")}
      aria-label={placeholder ?? t("common:search")}
      value={text}
      onChange={(event) => change(event.currentTarget.value)}
      w={{ base: "100%", sm: 300 }}
    />
  );
}
