import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { I18nextProvider } from "react-i18next";
import { MantineProvider } from "@mantine/core";
import { notifications } from "@mantine/notifications";
import { mantineTheme } from "@/lib/mantine-theme";
import { setAppNavigator } from "@/lib/app-navigator";
import { testI18n } from "@/test-utils";
import { problem } from "@/test/crm";
import { toastApiError } from "./use-toast";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@mantine/notifications", () => ({
  notifications: { show: vi.fn(() => "id"), hide: vi.fn() },
}));

/** The `message` the toast was shown with. */
const shownMessage = (): ReactNode => vi.mocked(notifications.show).mock.calls.at(-1)?.[0].message as ReactNode;

describe("toastApiError for plan errors", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setAppNavigator(null);
  });

  it("plan.limit_exceeded toasts 'Plan limitine ulaşıldı: Kullanıcı 5/5' with a link to Plan ve kullanım", async () => {
    const navigate = vi.fn();
    setAppNavigator(navigate);
    toastApiError(problem(402, { code: "plan.limit_exceeded", args: { limit: "users", max: 5, used: 5 } }));

    expect(notifications.show).toHaveBeenCalledWith(expect.objectContaining({ color: "red" }));
    render(
      <I18nextProvider i18n={testI18n}>
        <MantineProvider theme={mantineTheme} env="test">
          {shownMessage()}
        </MantineProvider>
      </I18nextProvider>
    );
    expect(screen.getByText("Plan limitine ulaşıldı: Kullanıcı 5/5")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Plan ve kullanım" }));
    expect(navigate).toHaveBeenCalledWith("/app/settings/plan");
  });

  it("other errors keep a plain text message", () => {
    toastApiError(problem(403, { code: "plan.module_disabled", args: { module: "commerce" } }));
    expect(shownMessage()).toBe("Bu modül planınıza dahil değil: Ticaret");

    toastApiError(problem(403, { code: "tenant.suspended", args: { reason: "trial_expired" } }));
    expect(shownMessage()).toContain("deneme süresi doldu");
  });
});
