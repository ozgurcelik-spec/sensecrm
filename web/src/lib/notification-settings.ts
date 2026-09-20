/** Client-side validation of the tenant notification settings (M8A); mirrors the server rules. */

export const SENDER_NAME_MAX = 100;
export const REPLY_TO_MAX = 254;

export type SettingsField = "senderName" | "replyTo";
export type SettingsErrors = Partial<Record<SettingsField, "tooLong" | "invalidChars" | "invalidEmail">>;

const hasControlChars = (value: string) => {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code < 0x20 || code === 0x7f) return true;
  }
  return false;
};

const EMAIL = /^[^\s@<>",;]+@[^\s@<>",;]+\.[^\s@<>",;]+$/;

/**
 * `senderName` <= 100 characters without control characters or `<>"`; `replyTo` empty or a valid
 * e-mail address <= 254 characters without line breaks. Values are expected trimmed.
 */
export function validateSettings(senderName: string, replyTo: string): SettingsErrors {
  const errors: SettingsErrors = {};
  if (senderName.length > SENDER_NAME_MAX) errors.senderName = "tooLong";
  else if (hasControlChars(senderName) || /[<>"]/.test(senderName)) errors.senderName = "invalidChars";
  if (replyTo.length > REPLY_TO_MAX) errors.replyTo = "tooLong";
  else if (replyTo !== "" && (hasControlChars(replyTo) || !EMAIL.test(replyTo))) errors.replyTo = "invalidEmail";
  return errors;
}
