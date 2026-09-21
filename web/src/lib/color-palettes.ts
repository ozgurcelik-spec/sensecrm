import { type MantineColorsTuple } from "@mantine/core";

export interface ColorPalette {
  id: string;
  name: string;
  description: string;
  primary: MantineColorsTuple;
  secondary: MantineColorsTuple;
}

/** Ocean Blue - Default brand palette */
const oceanBlue: ColorPalette = {
  id: "ocean-blue",
  name: "Ocean Blue",
  description: "Fresh and professional blue theme",
  primary: [
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
  ],
  secondary: [
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
  ],
};

/** Vibrant Purple */
const vibrantPurple: ColorPalette = {
  id: "vibrant-purple",
  name: "Vibrant Purple",
  description: "Bold and creative purple theme",
  primary: [
    "#faf5ff",
    "#f3e9ff",
    "#e9d5ff",
    "#d8b4fe",
    "#c084fc",
    "#a855f7",
    "#9333ea",
    "#7e22ce",
    "#6b21a8",
    "#581c87",
  ],
  secondary: [
    "#fef3c7",
    "#fde68a",
    "#fcd34d",
    "#fbbf24",
    "#f59e0b",
    "#f97316",
    "#ea580c",
    "#c2410c",
    "#9a3412",
    "#7c2d12",
  ],
};

/** Emerald Green */
const emeraldGreen: ColorPalette = {
  id: "emerald-green",
  name: "Emerald Green",
  description: "Fresh and natural green theme",
  primary: [
    "#f0fdf4",
    "#dcfce7",
    "#bbf7d0",
    "#86efac",
    "#4ade80",
    "#22c55e",
    "#16a34a",
    "#15803d",
    "#166534",
    "#14532d",
  ],
  secondary: [
    "#fef3c7",
    "#fde68a",
    "#fcd34d",
    "#fbbf24",
    "#f59e0b",
    "#f97316",
    "#ea580c",
    "#c2410c",
    "#9a3412",
    "#7c2d12",
  ],
};

/** Sunset Orange */
const sunsetOrange: ColorPalette = {
  id: "sunset-orange",
  name: "Sunset Orange",
  description: "Warm and energetic orange theme",
  primary: [
    "#fff7ed",
    "#ffedd5",
    "#fed7aa",
    "#fdba74",
    "#fb923c",
    "#f97316",
    "#ea580c",
    "#c2410c",
    "#9a3412",
    "#7c2d12",
  ],
  secondary: [
    "#faf5ff",
    "#f3e9ff",
    "#e9d5ff",
    "#d8b4fe",
    "#c084fc",
    "#a855f7",
    "#9333ea",
    "#7e22ce",
    "#6b21a8",
    "#581c87",
  ],
};

/** Rose Pink */
const rosePink: ColorPalette = {
  id: "rose-pink",
  name: "Rose Pink",
  description: "Modern and elegant pink theme",
  primary: [
    "#fff5f7",
    "#ffe4e6",
    "#fbcfe8",
    "#f8a0cf",
    "#f472b6",
    "#ec4899",
    "#db2777",
    "#be185d",
    "#9d174d",
    "#831843",
  ],
  secondary: [
    "#f0fdf4",
    "#dcfce7",
    "#bbf7d0",
    "#86efac",
    "#4ade80",
    "#22c55e",
    "#16a34a",
    "#15803d",
    "#166534",
    "#14532d",
  ],
};

/** Slate Gray */
const slateGray: ColorPalette = {
  id: "slate-gray",
  name: "Slate Gray",
  description: "Corporate and minimalist gray theme",
  primary: [
    "#f8fafc",
    "#f1f5f9",
    "#e2e8f0",
    "#cbd5e1",
    "#94a3b8",
    "#64748b",
    "#475569",
    "#334155",
    "#1e293b",
    "#0f172a",
  ],
  secondary: [
    "#fef3c7",
    "#fde68a",
    "#fcd34d",
    "#fbbf24",
    "#f59e0b",
    "#f97316",
    "#ea580c",
    "#c2410c",
    "#9a3412",
    "#7c2d12",
  ],
};

/** Cyan Teal */
const cyanTeal: ColorPalette = {
  id: "cyan-teal",
  name: "Cyan Teal",
  description: "Modern and tech-forward cyan theme",
  primary: [
    "#ecf9ff",
    "#cff9fe",
    "#a5f3fc",
    "#67e8f9",
    "#22d3ee",
    "#06b6d4",
    "#0891b2",
    "#0e7490",
    "#155e75",
    "#082f49",
  ],
  secondary: [
    "#fef3c7",
    "#fde68a",
    "#fcd34d",
    "#fbbf24",
    "#f59e0b",
    "#f97316",
    "#ea580c",
    "#c2410c",
    "#9a3412",
    "#7c2d12",
  ],
};

export const COLOR_PALETTES: ColorPalette[] = [
  oceanBlue,
  vibrantPurple,
  emeraldGreen,
  sunsetOrange,
  rosePink,
  slateGray,
  cyanTeal,
];

export const getColorPalette = (id: string): ColorPalette => {
  return COLOR_PALETTES.find((p) => p.id === id) || oceanBlue;
};
