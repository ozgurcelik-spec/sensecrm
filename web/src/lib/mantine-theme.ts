import { createTheme, type MantineColorsTuple } from "@mantine/core";

/** Sense brand blue (#008CF0 at index 6), shared with senseik. */
const brand: MantineColorsTuple = [
  "#f0f8fe",
  "#cfe9fc",
  "#addafa",
  "#8acaf8",
  "#64b9f6",
  "#39a6f3",
  "#008cf0",
  "#0070c0",
  "#005490",
  "#003860",
];

/** Kurumsal Lacivert (#280087 at index 7) - brand mark and emphasis surfaces. */
const navy: MantineColorsTuple = [
  "#f2f0f8",
  "#dbd4eb",
  "#c2b7dd",
  "#a999cf",
  "#8f7ac0",
  "#7258b0",
  "#53339f",
  "#280087",
  "#1c005e",
  "#100036",
];

export const mantineTheme = createTheme({
  primaryColor: "brand",
  primaryShade: { light: 6, dark: 5 },
  colors: { brand, navy },
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
