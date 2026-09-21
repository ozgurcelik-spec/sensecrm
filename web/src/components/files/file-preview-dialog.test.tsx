import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { fileItem } from "@/test/files";
import { installApi, problem, type MockClient } from "@/test/crm";
import { FilePreviewDialog } from "./file-preview-dialog";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

let createdTypes: string[];
let revoked: string[];
const onClose = vi.fn();
const onDownload = vi.fn();

function renderPreview(file = fileItem("f1", { name: "Rapor.pdf" })) {
  return renderWithProviders(<FilePreviewDialog file={file} onClose={onClose} onDownload={onDownload} />);
}

describe("FilePreviewDialog (blob preview, allow-list only)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    createdTypes = [];
    revoked = [];
    let counter = 0;
    URL.createObjectURL = vi.fn((blob: Blob | MediaSource) => {
      createdTypes.push((blob as Blob).type);
      counter += 1;
      return `blob:preview-${counter}`;
    });
    URL.revokeObjectURL = vi.fn((url: string) => void revoked.push(url));
  });
  afterEach(() => vi.restoreAllMocks());

  it("fetches the bytes with the inline disposition through the authenticated client and frames a PDF", async () => {
    installApi(client, { "GET /files/f1/content": () => new Blob(["%PDF-1.7"], { type: "application/pdf" }) });
    renderPreview();

    const frame = await screen.findByTitle("Rapor.pdf");
    expect(frame.tagName).toBe("IFRAME");
    expect(frame).toHaveAttribute("src", "blob:preview-1");
    const call = client.get.mock.calls.find(([url]) => url === "/files/f1/content");
    expect(call?.[1]).toMatchObject({ params: { disposition: "inline" }, responseType: "blob" });
    expect(createdTypes).toEqual(["application/pdf"]);
  });

  it.each(["image/png", "image/jpeg", "image/gif", "image/webp"])("shows %s as an image with the file name as alt text", async (type) => {
    installApi(client, { "GET /files/f1/content": () => new Blob(["x"], { type }) });
    renderPreview(fileItem("f1", { name: "Logo.png", extension: "png" }));

    const image = await screen.findByAltText("Logo.png");
    expect(image.tagName).toBe("IMG");
    expect(image).toHaveAttribute("src", "blob:preview-1");
    expect(createdTypes).toEqual([type]);
  });

  it("re-types the blob with the bare allow-listed type (no parameters)", async () => {
    installApi(client, {
      "GET /files/f1/content": () => new Blob(["%PDF"], { type: "application/pdf; charset=binary" }),
    });
    renderPreview();
    await screen.findByTitle("Rapor.pdf");
    expect(createdTypes).toEqual(["application/pdf"]);
  });

  it.each([
    "image/svg+xml",
    "text/html",
    "application/octet-stream",
    "text/plain",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    "",
  ])("a blob of type %j is never turned into a URL: 'cannot preview' with a download button", async (type) => {
    installApi(client, { "GET /files/f1/content": () => new Blob(["<svg onload=alert(1)>"], { type }) });
    renderPreview();

    expect(await screen.findByText("Önizleme yapılamıyor")).toBeInTheDocument();
    expect(URL.createObjectURL).not.toHaveBeenCalled();
    expect(document.querySelector("iframe, img")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "İndir" }));
    expect(onDownload).toHaveBeenCalledWith(expect.objectContaining({ id: "f1" }));
  });

  it("does not even request a file the server says cannot be previewed", async () => {
    installApi(client, {});
    renderPreview(fileItem("f2", { name: "Not.docx", extension: "docx", canPreview: false }));

    expect(await screen.findByText("Önizleme yapılamıyor")).toBeInTheDocument();
    expect(client.get).not.toHaveBeenCalled();
  });

  it("revokes the object URL when the dialog closes", async () => {
    installApi(client, { "GET /files/f1/content": () => new Blob(["%PDF"], { type: "application/pdf" }) });
    const view = renderPreview();
    await screen.findByTitle("Rapor.pdf");
    expect(revoked).toEqual([]);

    view.unmount();
    expect(revoked).toEqual(["blob:preview-1"]);
  });

  it("closes with Escape and with the close button", async () => {
    installApi(client, { "GET /files/f1/content": () => new Blob(["%PDF"], { type: "application/pdf" }) });
    renderPreview();
    await screen.findByTitle("Rapor.pdf");

    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    onClose.mockClear();
    await userEvent.click(screen.getByRole("button", { name: "Kapat" }));
    expect(onClose).toHaveBeenCalled();
  });

  it("shows the API error (e.g. quarantined, storage down) with a retry", async () => {
    installApi(client, {
      "GET /files/f1/content": () => problem(503, { code: "file.storage_unavailable", status: 503 }),
    });
    renderPreview();
    expect(
      await screen.findByText("Dosya depolama şu an kullanılamıyor. Biraz sonra tekrar deneyin.")
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Tekrar dene" })).toBeInTheDocument();
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });
});
