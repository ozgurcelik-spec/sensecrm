/**
 * Client-side mirror of the server password policy (C-SEC): 10-128 characters and must not contain
 * the e-mail local part. The server also rejects common passwords; its message is shown as-is.
 * Passwords are never trimmed.
 */
import { z } from "zod";

export const PASSWORD_MIN_LENGTH = 10;
export const PASSWORD_MAX_LENGTH = 128;

/** Local parts shorter than this are ignored (a 1-2 letter local part would reject nearly every password). */
const MIN_LOCAL_PART_LENGTH = 3;

export function emailLocalPart(email: string): string {
  const at = email.indexOf("@");
  return (at >= 0 ? email.slice(0, at) : email).trim().toLocaleLowerCase();
}

export function containsEmailLocalPart(password: string, email: string): boolean {
  const local = emailLocalPart(email);
  return local.length >= MIN_LOCAL_PART_LENGTH && password.toLocaleLowerCase().includes(local);
}

/** i18n keys (translated at render time with `{ min, max }`). */
export const PASSWORD_MESSAGES = {
  min: "auth:validation.passwordMin",
  max: "security:password.max",
  containsEmail: "security:password.containsEmail",
} as const;

/** zod field: length rules. Combine with `refinePasswordAgainstEmail` on the object schema. */
export function passwordField() {
  return z
    .string()
    .min(PASSWORD_MIN_LENGTH, PASSWORD_MESSAGES.min)
    .max(PASSWORD_MAX_LENGTH, PASSWORD_MESSAGES.max);
}

/** `superRefine` body for an object with `password`/`email` (or `newPassword` via `field`). */
export function refinePasswordAgainstEmail(
  password: string,
  email: string,
  ctx: z.RefinementCtx,
  field: string = "password"
): void {
  if (containsEmailLocalPart(password, email)) {
    ctx.addIssue({ code: "custom", path: [field], message: PASSWORD_MESSAGES.containsEmail });
  }
}
