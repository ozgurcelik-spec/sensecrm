import { API_URL, BASE_URL, PHASE, platformAdmin } from "./env.ts";
import { ApiClient } from "./api.ts";

/** Fails fast with a clear message when the stack is not there, instead of 100 timeouts. */
export default async function globalSetup(): Promise<void> {
  const health = await fetch(`${BASE_URL}/healthz`).catch(() => undefined);
  if (!health?.ok) {
    throw new Error(`The stack is not reachable at ${BASE_URL}. Start it with e2e/run.ps1 (or e2e/run.sh).`);
  }
  const config = (await (await fetch(`${API_URL}/auth/config`)).json()) as { signupEnabled: boolean };
  const expected = PHASE === "registration-open";
  if (config.signupEnabled !== expected) {
    throw new Error(`Phase "${PHASE}" expects signupEnabled=${expected} but the API reports ${config.signupEnabled}.`);
  }
  // The bootstrap admin must exist and be usable (create-platform-admin ran). Its UI language is set to English once, so console
  // tests can use stable English labels (the bootstrap default is Turkish). Nothing else ever changes this user.
  const admin = new ApiClient(platformAdmin());
  await admin.login();
  await admin.patch("/me", { locale: "en" });
}
