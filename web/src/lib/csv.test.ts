import { afterEach, describe, expect, it, vi } from "vitest";
import { CSV_BOM, csvBlob, csvDelimiter, downloadCsv, toCsv } from "./csv";

describe("toCsv", () => {
  it("uses ';' and decimal commas for Turkish (what Excel expects there)", () => {
    const csv = toCsv(
      ["Dönem", "Tutar"],
      [
        ["2026-09", 12500.5],
        ["2026-10", 3],
      ],
      "tr-TR"
    );
    expect(csv).toBe("Dönem;Tutar\r\n2026-09;12500,5\r\n2026-10;3");
  });

  it("uses ',' and decimal points for English", () => {
    expect(csvDelimiter("en-US")).toBe(",");
    expect(toCsv(["Period", "Amount"], [["2026-09", 12500.5]], "en-US")).toBe(
      "Period,Amount\r\n2026-09,12500.5"
    );
  });

  it("keeps large numbers ungrouped so a spreadsheet reads them as numbers", () => {
    expect(toCsv(["n"], [[1234567.891]], "tr-TR")).toBe("n\r\n1234567,89");
  });

  it("quotes cells that contain the delimiter, a quote or a line break, doubling the quotes", () => {
    const csv = toCsv(["a", "b", "c"], [["x;y", 'say "hi"', "line1\nline2"]], "tr-TR");
    expect(csv).toBe('a;b;c\r\n"x;y";"say ""hi""";"line1\nline2"');
    // A comma is only special where the delimiter is a comma.
    expect(toCsv(["a"], [["1,5"]], "tr-TR")).toBe("a\r\n1,5");
    expect(toCsv(["a"], [["1,5"]], "en-US")).toBe('a\r\n"1,5"');
  });

  it("neutralises spreadsheet formulas in text, but not negative numbers", () => {
    const csv = toCsv(["v"], [["=1+1"], ["+cmd"], ["-x"], ["@sum"], [-5]], "en-US");
    expect(csv).toBe("v\r\n'=1+1\r\n'+cmd\r\n'-x\r\n'@sum\r\n-5");
  });

  it("writes empty cells for null, undefined and non-finite numbers", () => {
    expect(toCsv(["a", "b", "c"], [[null, undefined, Number.NaN]], "en-US")).toBe("a,b,c\r\n,,");
  });
});

describe("csv download", () => {
  afterEach(() => vi.restoreAllMocks());

  it("prefixes the UTF-8 byte order mark so Excel reads Turkish characters", async () => {
    const bytes = new Uint8Array(await csvBlob("Şişli").arrayBuffer());
    expect([...bytes.slice(0, 3)]).toEqual([0xef, 0xbb, 0xbf]);
    expect(CSV_BOM).toBe("﻿");
    expect(new TextDecoder("utf-8", { ignoreBOM: true }).decode(bytes)).toBe("﻿Şişli");
    expect(csvBlob("x").type).toBe("text/csv;charset=utf-8");
  });

  it("downloads through a temporary link and releases the object URL", () => {
    const create = vi.fn(() => "blob:csv-1");
    const revoke = vi.fn();
    Object.assign(URL, { createObjectURL: create, revokeObjectURL: revoke });
    let downloaded: { name: string; href: string } | undefined;
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (
      this: HTMLAnchorElement
    ) {
      downloaded = { name: this.download, href: this.href };
    });

    downloadCsv("rapor.csv", "a;b");

    expect(create).toHaveBeenCalledTimes(1);
    expect(downloaded).toEqual({ name: "rapor.csv", href: "blob:csv-1" });
    expect(revoke).toHaveBeenCalledWith("blob:csv-1");
    expect(document.querySelector("a[download]")).toBeNull();
  });
});
