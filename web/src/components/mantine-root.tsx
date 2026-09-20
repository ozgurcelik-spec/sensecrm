import { useMemo, type ReactNode } from "react";
import { MantineProvider } from "@mantine/core";
import { useColorScheme } from "@mantine/hooks";
import { Notifications } from "@mantine/notifications";
import { getColorPalette } from "@/lib/color-palettes";
import { createMantineTheme } from "@/lib/mantine-theme";
import { useUIStore } from "@/store/ui.store";

/** Mantine providers: theme, color scheme from the UI store (light/dark/system), notifications. */
export function MantineRoot({ children }: { children: ReactNode }) {
  const theme = useUIStore((state) => state.theme);
  const colorPalette = useUIStore((state) => state.colorPalette);
  const systemScheme = useColorScheme();
  const colorScheme = theme === "system" ? systemScheme : theme;

  const mantineTheme = useMemo(
    () => createMantineTheme(getColorPalette(colorPalette)),
    [colorPalette]
  );

  return (
    <MantineProvider theme={mantineTheme} forceColorScheme={colorScheme}>
      <Notifications position="top-right" limit={3} />
      {children}
    </MantineProvider>
  );
}
