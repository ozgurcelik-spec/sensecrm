import { useTranslation } from "react-i18next";
import { Button, Menu } from "@mantine/core";
import { Check, Languages } from "lucide-react";
import { SUPPORTED_LOCALES } from "@/lib/locale";
import { useChangeLanguage } from "@/hooks/use-change-language";

/** TR/EN switch used in the app header and on the auth screens. */
export function LanguageMenu() {
  const { t } = useTranslation(["common"]);
  const { current, change } = useChangeLanguage();

  return (
    <Menu position="bottom-end" width={160}>
      <Menu.Target>
        <Button
          variant="subtle"
          color="gray"
          size="compact-sm"
          leftSection={<Languages size={16} />}
          aria-label={t("common:shell.language")}
        >
          {current.toUpperCase()}
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>{t("common:shell.language")}</Menu.Label>
        {SUPPORTED_LOCALES.map((locale) => (
          <Menu.Item
            key={locale}
            onClick={() => void change(locale)}
            rightSection={locale === current ? <Check size={14} /> : null}
          >
            {t(`common:languages.${locale}`)}
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}
