/**
 * Test helpers: an in-memory i18next instance loaded from the real locale files (no HTTP backend)
 * and a render wrapper with the app providers.
 */
import type { ReactElement, ReactNode } from "react";
import { render } from "@testing-library/react";
import i18next, { type i18n as I18n } from "i18next";
import { I18nextProvider, initReactI18next } from "react-i18next";
import { MantineProvider } from "@mantine/core";
import { MemoryRouter } from "react-router";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { mantineTheme } from "@/lib/mantine-theme";

const localeFiles = import.meta.glob<Record<string, unknown>>("../public/locales/*/*.json", {
  eager: true,
  import: "default",
});

function loadResources() {
  const resources: Record<string, Record<string, Record<string, unknown>>> = {};
  for (const [path, content] of Object.entries(localeFiles)) {
    const match = /locales\/(\w+)\/(\w+)\.json$/.exec(path);
    if (!match) continue;
    const [, lng, ns] = match as unknown as [string, string, string];
    (resources[lng] ??= {})[ns] = content;
  }
  return resources;
}

export function createTestI18n(lng: "tr" | "en" = "tr"): I18n {
  const instance = i18next.createInstance();
  void instance.use(initReactI18next).init({
    lng,
    fallbackLng: "tr",
    defaultNS: "common",
    resources: loadResources(),
    interpolation: { escapeValue: false },
    initAsync: false,
  });
  return instance;
}

/** Shared instance: tests mock `@/i18n` with this so non-React code (error messages) is translated too. */
export const testI18n = createTestI18n();

export function renderWithProviders(ui: ReactElement, { route = "/" }: { route?: string } = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <I18nextProvider i18n={testI18n}>
        <QueryClientProvider client={queryClient}>
          {/* env="test": no transitions/portals, so modals and dropdowns render synchronously. */}
          <MantineProvider theme={mantineTheme} env="test">
            <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>
          </MantineProvider>
        </QueryClientProvider>
      </I18nextProvider>
    );
  }
  return render(ui, { wrapper: Wrapper });
}
