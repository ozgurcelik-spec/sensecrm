import { describe, expect, it } from "vitest";
import {
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
  containsEmailLocalPart,
  emailLocalPart,
  passwordField,
} from "./password-policy";

describe("password policy", () => {
  it("mirrors the server length rules (10-128)", () => {
    expect(PASSWORD_MIN_LENGTH).toBe(10);
    expect(PASSWORD_MAX_LENGTH).toBe(128);
    const field = passwordField();
    expect(field.safeParse("a".repeat(9)).success).toBe(false);
    expect(field.safeParse("a".repeat(10)).success).toBe(true);
    expect(field.safeParse("a".repeat(128)).success).toBe(true);
    expect(field.safeParse("a".repeat(129)).success).toBe(false);
  });

  it("does not trim passwords (spaces count)", () => {
    expect(passwordField().safeParse("         a").success).toBe(true);
  });

  it("detects the e-mail local part case-insensitively", () => {
    expect(emailLocalPart("Ada.Lovelace@example.com")).toBe("ada.lovelace");
    expect(containsEmailLocalPart("xxADA.lovelacexx1", "Ada.Lovelace@example.com")).toBe(true);
    expect(containsEmailLocalPart("completely-different-1", "ada@example.com")).toBe(false);
  });

  it("ignores very short local parts", () => {
    expect(containsEmailLocalPart("abcdefghijk", "a@example.com")).toBe(false);
  });
});
