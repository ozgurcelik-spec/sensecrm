import { beforeEach, describe, expect, it, vi } from "vitest";
import { apiClient } from "@/lib/api-client";
import { fileItem, makeFile } from "@/test/files";
import type { MockClient } from "@/test/crm";
import { deleteFile, fetchFileBlob, getFileLimits, getFilesUsage, listFiles, renameFile, uploadFile } from "./files.service";

vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

describe("files service (HTTP contract of docs/plan/m8c-dosya-ekleri.md)", () => {
  beforeEach(() => vi.clearAllMocks());

  it("lists with the record and paging in the query, dropping empty values", async () => {
    client.get.mockResolvedValue({ data: { items: [], page: 1, pageSize: 20, totalCount: 0 } });
    await listFiles({ recordType: "quote", recordId: "q1", q: "", sort: "-uploadedAt", page: 2, pageSize: 20 });
    expect(client.get).toHaveBeenCalledWith("/files", {
      params: { recordType: "quote", recordId: "q1", sort: "-uploadedAt", page: 2, pageSize: 20 },
    });
  });

  it("uploads one multipart `file` part with the record as query, the JSON default header replaced and a 5 minute timeout", async () => {
    client.post.mockResolvedValue({ data: { items: [fileItem("n1")], failed: [] } });
    const file = makeFile("Teklif.pdf", 10);
    const controller = new AbortController();
    const result = await uploadFile("deal", "d1", file, { signal: controller.signal });

    const [url, body, config] = client.post.mock.calls[0] as [string, FormData, Record<string, unknown>];
    expect(url).toBe("/files");
    expect(body).toBeInstanceOf(FormData);
    expect([...body.keys()]).toEqual(["file"]);
    expect(body.get("file")).toMatchObject({ name: "Teklif.pdf" });
    expect(config).toMatchObject({
      params: { recordType: "deal", recordId: "d1" },
      headers: { "Content-Type": "multipart/form-data" },
      timeout: 300000,
      signal: controller.signal,
    });
    expect(result.items).toHaveLength(1);
    expect(result.failed).toEqual([]);
  });

  it("reports progress as whole percent, from axios' fraction or from loaded / total", async () => {
    const percents: number[] = [];
    client.post.mockImplementation(async (_url: string, _body: unknown, config: { onUploadProgress: (e: object) => void }) => {
      config.onUploadProgress({ loaded: 1, total: 4, progress: 0.25 });
      config.onUploadProgress({ loaded: 1, total: 3 });
      config.onUploadProgress({ loaded: 5, total: 4, progress: 1.25 });
      config.onUploadProgress({ loaded: 5 });
      return { data: { items: [], failed: [] } };
    });
    await uploadFile("account", "a1", makeFile("a.pdf"), { onProgress: (p) => percents.push(p) });
    expect(percents).toEqual([25, 33, 100]);
  });

  it("tolerates a body without items / failed", async () => {
    client.post.mockResolvedValue({ data: undefined });
    expect(await uploadFile("account", "a1", makeFile("a.pdf"))).toEqual({ items: [], failed: [] });
  });

  it("fetches the content as a blob with the disposition, the id path-encoded", async () => {
    const blob = new Blob(["x"]);
    client.get.mockResolvedValue({ data: blob });
    expect(await fetchFileBlob("a/b", "inline")).toBe(blob);
    expect(client.get).toHaveBeenCalledWith("/files/a%2Fb/content", expect.objectContaining({
      params: { disposition: "inline" },
      responseType: "blob",
    }));
  });

  it("renames with PATCH { name }, deletes with DELETE, reads usage and limits", async () => {
    client.patch.mockResolvedValue({ data: undefined });
    client.delete.mockResolvedValue({ data: undefined });
    client.get.mockResolvedValue({ data: {} });
    await renameFile("f1", "Yeni.pdf");
    await deleteFile("f1");
    await getFilesUsage();
    await getFileLimits();
    expect(client.patch).toHaveBeenCalledWith("/files/f1", { name: "Yeni.pdf" });
    expect(client.delete).toHaveBeenCalledWith("/files/f1");
    expect(client.get).toHaveBeenCalledWith("/files/usage");
    expect(client.get).toHaveBeenCalledWith("/files/limits");
  });
});
