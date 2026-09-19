import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, setPermissions, type MockClient } from "@/test/crm";
import { toast, toastApiError } from "@/hooks/use-toast";
import SlaSettingsPage from "./sla";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const POLICIES = [
  { priority: "low", firstResponseMinutes: 1440, resolutionMinutes: 10080 },
  { priority: "normal", firstResponseMinutes: 480, resolutionMinutes: 4320 },
  { priority: "high", firstResponseMinutes: 240, resolutionMinutes: 1440 },
  { priority: "urgent", firstResponseMinutes: 60, resolutionMinutes: 240 },
];

const field = (priority: string, kind: "İlk yanıt" | "Çözüm") =>
  screen.getByRole("textbox", { name: `${priority} - ${kind} (dk)` });

async function setMinutes(input: HTMLElement, value: string) {
  await userEvent.clear(input);
  await userEvent.type(input, value);
}

describe("SlaSettingsPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["org.settings.manage"]);
    installApi(client, {
      "GET /service/sla-policies": () => POLICIES,
      "PUT /service/sla-policies": () => undefined,
    });
  });
  afterEach(clearSession);

  it("shows the four priorities with their minutes and a human readable equivalent", async () => {
    renderWithProviders(<SlaSettingsPage />);
    expect(await screen.findAllByTestId("sla-row")).toHaveLength(4);

    const [low, normal, high, urgent] = screen.getAllByTestId("sla-row");
    expect(within(low as HTMLElement).getByText("Düşük")).toBeInTheDocument();
    expect(within(normal as HTMLElement).getByText("Normal")).toBeInTheDocument();
    expect(within(high as HTMLElement).getByText("Yüksek")).toBeInTheDocument();
    expect(within(urgent as HTMLElement).getByText("Acil")).toBeInTheDocument();

    expect(field("Normal", "İlk yanıt")).toHaveValue("480");
    expect(within(normal as HTMLElement).getByText("= 8 sa")).toBeInTheDocument();
    expect(within(low as HTMLElement).getByText("= 7 gün")).toBeInTheDocument();
    expect(within(urgent as HTMLElement).getByText("= 1 sa")).toBeInTheDocument();
    expect(screen.getByText(/duvar saati dakikasıdır/)).toBeInTheDocument();
  });

  it("keeps Save disabled until something changes", async () => {
    renderWithProviders(<SlaSettingsPage />);
    const save = await screen.findByRole("button", { name: "Kaydet" });
    expect(save).toBeDisabled();

    await setMinutes(field("Yüksek", "Çözüm"), "1000");
    expect(save).toBeEnabled();

    // Back to the saved value: nothing to save again.
    await setMinutes(field("Yüksek", "Çözüm"), "1440");
    expect(save).toBeDisabled();
  });

  it("PUTs all four policies in low, normal, high, urgent order", async () => {
    renderWithProviders(<SlaSettingsPage />);
    await screen.findAllByTestId("sla-row");
    await setMinutes(field("Acil", "İlk yanıt"), "30");
    await setMinutes(field("Acil", "Çözüm"), "120");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.put).toHaveBeenCalled());
    expect(client.put.mock.calls[0]).toEqual([
      "/service/sla-policies",
      {
        policies: [
          POLICIES[0],
          POLICIES[1],
          POLICIES[2],
          { priority: "urgent", firstResponseMinutes: 30, resolutionMinutes: 120 },
        ],
      },
    ]);
    await waitFor(() => expect(toast).toHaveBeenCalled());
  });

  it("validates on the client like the server: 1..525600 and first response <= resolution", async () => {
    renderWithProviders(<SlaSettingsPage />);
    await screen.findAllByTestId("sla-row");

    await setMinutes(field("Normal", "İlk yanıt"), "5000");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    expect(
      await screen.findByText("İlk yanıt süresi çözüm süresinden uzun olamaz")
    ).toBeInTheDocument();
    expect(client.put).not.toHaveBeenCalled();

    await setMinutes(field("Normal", "İlk yanıt"), "480");
    await setMinutes(field("Düşük", "Çözüm"), "525601");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    expect(
      await screen.findAllByText("1 ile 525600 arasında tam sayı girin")
    ).not.toHaveLength(0);
    expect(client.put).not.toHaveBeenCalled();

    // Empty is invalid too.
    await setMinutes(field("Düşük", "Çözüm"), "10080");
    await userEvent.clear(field("Acil", "İlk yanıt"));
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    expect(await screen.findAllByText("1 ile 525600 arasında tam sayı girin")).not.toHaveLength(0);
    expect(client.put).not.toHaveBeenCalled();
  });

  it("maps the server's policies[i].field errors onto the rows", async () => {
    installApi(client, {
      "GET /service/sla-policies": () => POLICIES,
      "PUT /service/sla-policies": () =>
        problem(400, {
          code: "validation",
          errors: { "Policies[2].FirstResponseMinutes": ["Sunucu: çok büyük"] },
        }),
    });
    renderWithProviders(<SlaSettingsPage />);
    await screen.findAllByTestId("sla-row");
    await setMinutes(field("Yüksek", "İlk yanıt"), "200");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    const error = await screen.findByText("Sunucu: çok büyük");
    expect(within(screen.getAllByTestId("sla-row")[2] as HTMLElement).getByText("Sunucu: çok büyük")).toBe(error);
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("shows a general policies error and falls back to a toast for unknown errors", async () => {
    installApi(client, {
      "GET /service/sla-policies": () => POLICIES,
      "PUT /service/sla-policies": () =>
        problem(400, { code: "validation", errors: { policies: ["Dört öncelik gerekli"] } }),
    });
    renderWithProviders(<SlaSettingsPage />);
    await screen.findAllByTestId("sla-row");
    await setMinutes(field("Yüksek", "Çözüm"), "1500");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    expect(await screen.findByText("Dört öncelik gerekli")).toBeInTheDocument();

    installApi(client, {
      "GET /service/sla-policies": () => POLICIES,
      "PUT /service/sla-policies": () => problem(500, { code: "unknown" }),
    });
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalled());
  });
});
