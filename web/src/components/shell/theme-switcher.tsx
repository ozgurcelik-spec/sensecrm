import { useTranslation } from "react-i18next";
import { ColorSwatch, Group, Menu } from "@mantine/core";
import { Check } from "lucide-react";
import { COLOR_PALETTES } from "@/lib/color-palettes";
import { useUIStore } from "@/store/ui.store";

/** Palette picker rendered inside the user menu (flat items: a nested menu would close its parent). */
export function ThemeSwitcher() {
  const { t } = useTranslation(["common"]);
  const colorPalette = useUIStore((state) => state.colorPalette);
  const setColorPalette = useUIStore((state) => state.setColorPalette);

  return (
    <>
      <Menu.Label>{t("common:shell.colorTheme", { defaultValue: "Renk teması" })}</Menu.Label>
      {COLOR_PALETTES.map((palette) => (
        <Menu.Item
          key={palette.id}
          closeMenuOnClick={false}
          onClick={() => setColorPalette(palette.id)}
          leftSection={
            <Group gap={2} wrap="nowrap">
              <ColorSwatch color={palette.primary[6]} size={16} />
              <ColorSwatch color={palette.secondary[6]} size={16} />
            </Group>
          }
          rightSection={colorPalette === palette.id ? <Check size={14} /> : null}
        >
          {palette.name}
        </Menu.Item>
      ))}
    </>
  );
}
