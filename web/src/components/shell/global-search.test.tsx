import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, page, setPermissions, type MockClient } from "@/test/crm";
import { GlobalSearch } from "./global-search";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

describe("GlobalSearch", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /leads": () => page([{ id: "l1", fullName: "Ayşe Acar", company: "Acme" }]),
      "GET /deals": () => page([{ id: "d1", name: "Acme yıllık", accountName: "Acme" }]),
    });
  });
  afterEach(clearSession);

  it("searches only what the user may read and links each hit to its record", async () => {
    setPermissions(["crm.leads.read", "crm.deals.read"]);
    renderWithProviders(<GlobalSearch />);

    await userEvent.type(screen.getByRole("textbox", { name: "Ara" }), "acme");

    expect(await screen.findByRole("link", { name: /Ayşe Acar/ })).toHaveAttribute(
      "href",
      "/app/leads/l1"
    );
    expect(screen.getByRole("link", { name: /Acme yıllık/ })).toHaveAttribute(
      "href",
      "/app/deals/d1"
    );
    const urls = client.get.mock.calls.map(([u]) => u);
    expect(urls).not.toContain("/contacts");
    expect(urls).not.toContain("/accounts");
    expect(client.get).toHaveBeenCalledWith(
      "/leads",
      expect.objectContaining({ params: expect.objectContaining({ q: "acme" }) })
    );
  });

  it("does not request anything below two characters", async () => {
    setPermissions(["crm.leads.read"]);
    renderWithProviders(<GlobalSearch />);

    await userEvent.type(screen.getByRole("textbox", { name: "Ara" }), "a");
    await waitFor(() => expect(screen.getByRole("textbox", { name: "Ara" })).toHaveValue("a"));
    expect(client.get).not.toHaveBeenCalled();
  });
});
