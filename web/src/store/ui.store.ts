import { create } from "zustand";
import { persist } from "zustand/middleware";

export type Theme = "light" | "dark" | "system";

interface UIStore {
  theme: Theme;
  setTheme: (theme: Theme) => void;
  /** Collapsed left navigation (icon rail) - a per-viewer convenience. */
  navCollapsed: boolean;
  toggleNav: () => void;
}

export const useUIStore = create<UIStore>()(
  persist(
    (set) => ({
      theme: "light",
      setTheme: (theme) => set({ theme }),
      navCollapsed: false,
      toggleNav: () => set((state) => ({ navCollapsed: !state.navCollapsed })),
    }),
    { name: "ui-store" }
  )
);
