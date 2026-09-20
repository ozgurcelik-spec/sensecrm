import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, type MockClient } from "@/test/crm";
import { toast, toastApiError } from "@/hooks/use-toast";
import { UsageExportDialog } from "./usage-export-dialog";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const CSV = new Blob(["day,tenantId\n"], { type: "text/csv" });

const exportCalls = () => client.get.mock.calls.filter(([url]) => url === "/platform/usage/export");
const download = () => screen.getByRole("button", { name: "İndir" });

describe("UsageExportDialog", () => {
  const createObjectURL = vi.fn(() => "blob:usage");
  const revokeObjectURL = vi.fn();
  let clicked: { download: string; href: string }[];

  beforeEach(() => {
    vi.clearAllMocks();
    clicked = [];
    Object.defineProperty(URL, "createObjectURL", { value: createObjectURL, configurable: true });
    Object.defineProperty(URL, "revokeObjectURL", { value: revokeObjectURL, configurable: true });
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push({ download: this.download, href: this.href });
    });
    installApi(client, { "GET /platform/usage/export": () => CSV });
  });
  afterEach(() => {
    vi.restoreAllMocks();
    clearSession();
  });

  it("without dates it asks for the server default (previous month) and saves the CSV", async () => {
    const onClose = vi.fn();
    renderWithProviders(<UsageExportDialog onClose={onClose} />);
    await userEvent.click(download());

    await waitFor(() => expect(exportCalls()).toHaveLength(1));
    expect(exportCalls()[0]?.[1]).toEqual({ params: {}, responseType: "blob" });
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(createObjectURL).toHaveBeenCalledWith(CSV);
    expect(clicked).toEqual([{ download: "usage-previous-month.csv", href: expect.stringContaining("blob:usage") }]);
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }));
  });

  it("sends the chosen inclusive UTC day range and names the file after it", async () => {
    renderWithProviders(<UsageExportDialog onClose={vi.fn()} />);
    await userEvent.type(screen.getByLabelText("Başlangıç günü"), "2026-08-01");
    await userEvent.type(screen.getByLabelText("Bitiş günü"), "2026-08-31");
    await userEvent.click(download());

    await waitFor(() => expect(exportCalls()).toHaveLength(1));
    expect(exportCalls()[0]?.[1]).toEqual({
      params: { from: "2026-08-01", to: "2026-08-31" },
      responseType: "blob",
    });
    await waitFor(() => expect(clicked).toHaveLength(1));
    expect(clicked[0]?.download).toBe("usage-2026-08-01-2026-08-31.csv");
  });

  it("blocks an incomplete, reversed or over-long range before any request", async () => {
    renderWithProviders(<UsageExportDialog onClose={vi.fn()} />);
    const from = screen.getByLabelText("Başlangıç günü");
    const to = screen.getByLabelText("Bitiş günü");

    await userEvent.type(from, "2026-08-01");
    expect(await screen.findByRole("alert")).toHaveTextContent("İki tarihi de girin");
    expect(download()).toBeDisabled();

    await userEvent.type(to, "2026-08-31");
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(download()).toBeEnabled();

    // 2025-07-01 .. 2026-08-31 is more than 400 days.
    await userEvent.clear(from);
    await userEvent.type(from, "2025-07-01");
    expect(await screen.findByRole("alert")).toHaveTextContent("en çok 400 gün");
    expect(download()).toBeDisabled();
    expect(exportCalls()).toHaveLength(0);

    // Exactly 400 days is fine.
    await userEvent.clear(from);
    await userEvent.type(from, "2025-07-28");
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(download()).toBeEnabled();
  });

  it("toasts a failed export and stays open", async () => {
    installApi(client, { "GET /platform/usage/export": () => problem(403, { code: "forbidden" }) });
    const onClose = vi.fn();
    renderWithProviders(<UsageExportDialog onClose={onClose} />);
    await userEvent.click(download());

    await waitFor(() => expect(toastApiError).toHaveBeenCalled());
    expect(onClose).not.toHaveBeenCalled();
    expect(clicked).toHaveLength(0);
  });
});
