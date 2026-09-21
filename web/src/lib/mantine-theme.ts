import { createTheme } from "@mantine/core";
import { COLOR_PALETTES, type ColorPalette } from "./color-palettes";

/** `brand` = palette primary, `navy` = palette accent; components reference these names. */
export function createMantineTheme(palette: ColorPalette) {
  return createTheme({
    primaryColor: "brand",
    primaryShade: { light: 6, dark: 5 },
    colors: { brand: palette.primary, navy: palette.secondary },
    fontFamily: "'Inter Variable', Inter, system-ui, -apple-system, 'Segoe UI', sans-serif",
    headings: {
      fontFamily: "'Inter Variable', Inter, system-ui, -apple-system, 'Segoe UI', sans-serif",
    },
    defaultRadius: "md",
    cursorType: "pointer",
    components: {
      Card: {
        defaultProps: { radius: "lg", shadow: "xs" },
        styles: {
          root: {
            borderColor: "light-dark(var(--mantine-color-gray-2), var(--mantine-color-dark-4))",
          },
        },
      },
    },
  });
}

export const mantineTheme = createMantineTheme(COLOR_PALETTES[0]);
