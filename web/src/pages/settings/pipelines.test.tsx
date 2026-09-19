import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { toastApiError } from "@/hooks/use-toast";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, setPermissions, type MockClient } from "@/test/crm";
import PipelinesPage from "./pipelines";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const PIPELINE = {
  id: "p1",
  name: "Satış",
  isDefault: true,
  stages: [
    { id: "s1", name: "Nitelendirme", order: 1, probability: 10, kind: "open" },
    { id: "s2", name: "Teklif", order: 2, probability: 50, kind: "open" },
    { id: "s3", name: "Kazanıldı", order: 3, probability: 100, kind: "won" },
    { id: "s4", name: "Kaybedildi", order: 4, probability: 0, kind: "lost" },
  ],
};

const stageNames = () =>
  screen
    .getAllByRole("textbox", { name: "Aşama adı" })
    .map((input) => (input as HTMLInputElement).value);

describe("PipelinesPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /pipelines": () => [PIPELINE],
      "PUT /pipelines/p1/stages": () => undefined,
    });
  });
  afterEach(clearSession);

  it("is read-only without the settings management permission", async () => {
    setPermissions(["crm.deals.read"]);
    renderWithProviders(<PipelinesPage />);

    await screen.findByDisplayValue("Nitelendirme");
    expect(screen.queryByRole("button", { name: "Aşama ekle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Aşamaları kaydet" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Yeni huni" })).not.toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: "Huni adı" })).toBeDisabled();
    expect(screen.getAllByRole("textbox", { name: "Aşama adı" })[0]).toBeDisabled();
  });

  it("reorders, renames and adds stages, then saves them in order with their ids", async () => {
    setPermissions(["crm.deals.read", "org.settings.manage"]);
    renderWithProviders(<PipelinesPage />);
    await screen.findByDisplayValue("Nitelendirme");
    const save = screen.getByRole("button", { name: "Aşamaları kaydet" });
    expect(save).toBeDisabled();

    // Move "Teklif" above "Nitelendirme".
    await userEvent.click(screen.getByRole("button", { name: "2. aşamayı yukarı taşı" }));
    expect(stageNames().slice(0, 2)).toEqual(["Teklif", "Nitelendirme"]);

    // Rename and change the probability of the first row.
    const first = screen.getAllByRole("textbox", { name: "Aşama adı" })[0] as HTMLElement;
    await userEvent.clear(first);
    await userEvent.type(first, "Teklif verildi");

    // Add a new open stage.
    await userEvent.click(screen.getByRole("button", { name: "Aşama ekle" }));
    const inputs = screen.getAllByRole("textbox", { name: "Aşama adı" });
    await userEvent.type(inputs.at(-1) as HTMLElement, "Sözleşme");

    await userEvent.click(save);

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    const [url, body] = client.put.mock.calls[0] as [
      string,
      { stages: Array<Record<string, unknown>> },
    ];
    expect(url).toBe("/pipelines/p1/stages");
    expect(body.stages).toEqual([
      { id: "s2", name: "Teklif verildi", probability: 50, kind: "open" },
      { id: "s1", name: "Nitelendirme", probability: 10, kind: "open" },
      { id: "s3", name: "Kazanıldı", probability: 100, kind: "won" },
      { id: "s4", name: "Kaybedildi", probability: 0, kind: "lost" },
      { id: undefined, name: "Sözleşme", probability: 0, kind: "open" },
    ]);
  });

  it("blocks saving without exactly one won and one lost stage", async () => {
    setPermissions(["crm.deals.read", "org.settings.manage"]);
    renderWithProviders(<PipelinesPage />);
    await screen.findByDisplayValue("Nitelendirme");

    await userEvent.click(screen.getByRole("button", { name: "4. aşamayı sil" }));
    await userEvent.click(screen.getByRole("button", { name: "Aşamaları kaydet" }));

    const alert = await screen.findByRole("alert");
    expect(within(alert).getByText(/Tam olarak bir "Kaybedildi" aşaması/)).toBeInTheDocument();
    expect(client.put).not.toHaveBeenCalled();
  });

  it("blocks saving a stage without a name", async () => {
    setPermissions(["crm.deals.read", "org.settings.manage"]);
    renderWithProviders(<PipelinesPage />);
    await screen.findByDisplayValue("Nitelendirme");

    await userEvent.clear(screen.getAllByRole("textbox", { name: "Aşama adı" })[0] as HTMLElement);
    await userEvent.click(screen.getByRole("button", { name: "Aşamaları kaydet" }));

    expect(await screen.findByText("Her aşamanın adı olmalıdır.")).toBeInTheDocument();
    expect(client.put).not.toHaveBeenCalled();
  });

  it("reports a server rejection such as pipeline.stage_in_use", async () => {
    setPermissions(["crm.deals.read", "org.settings.manage"]);
    const conflict = problem(409, {
      status: 409,
      title: "Conflict",
      code: "pipeline.stage_in_use",
    });
    installApi(client, {
      "GET /pipelines": () => [PIPELINE],
      "PUT /pipelines/p1/stages": () => conflict,
    });
    renderWithProviders(<PipelinesPage />);
    await screen.findByDisplayValue("Nitelendirme");

    await userEvent.click(screen.getByRole("button", { name: "2. aşamayı sil" }));
    await userEvent.click(screen.getByRole("button", { name: "Aşamaları kaydet" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(conflict));
  });
});
