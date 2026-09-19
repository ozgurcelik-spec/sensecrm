import { describe, expect, it } from "vitest";
import { isHttpUrl, safeHttpHref } from "./url";

describe("http(s) URL helpers", () => {
  it.each([
    "http://acme.example",
    "https://acme.example/path?q=1",
    "HTTPS://ACME.example",
    " https://a.io ",
  ])("accepts %s", (value) => {
    expect(isHttpUrl(value)).toBe(true);
    expect(safeHttpHref(value)).toMatch(/^https?:\/\//);
  });

  it.each([
    "javascript:alert(1)",
    "JavaScript:alert(1)",
    "data:text/html,x",
    "vbscript:x",
    "ftp://acme.example",
    "//acme.example",
    "acme.example",
    "https://",
    "http:acme.example",
    "",
  ])("rejects %s", (value) => {
    expect(isHttpUrl(value)).toBe(false);
    expect(safeHttpHref(value)).toBeUndefined();
  });

  it("returns undefined for empty values", () => {
    expect(safeHttpHref(undefined)).toBeUndefined();
    expect(safeHttpHref(null)).toBeUndefined();
  });
});
