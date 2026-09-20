import { create } from "zustand";
import { persist } from "zustand/middleware";

export type Theme = "light" | "dark" | "system";

interface UIStore {
  theme: Theme;
  setTheme: (theme: Theme) => void;
  /** Color palette ID (e.g., "ocean-blue", "vibrant-purple") */
  colorPalette: string;
  setColorPalette: (paletteId: string) => void;
  /** Collapsed left navigation (icon rail) - a per-viewer convenience. */
  navCollapsed: boolean;
  toggleNav: () => void;
}

export const useUIStore = create<UIStore>()(
  persist(
    (set) => ({
      theme: "light",
      setTheme: (theme) => set({ theme }),
      colorPalette: "ocean-blue",
      setColorPalette: (paletteId) => set({ colorPalette: paletteId }),
      navCollapsed: false,
      toggleNav: () => set((state) => ({ navCollapsed: !state.navCollapsed })),
    }),
    { name: "ui-store" }
  )
);
