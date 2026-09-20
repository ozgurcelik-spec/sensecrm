/** Settings handed over by e2e/run.sh / run.ps1 (or by hand when you point the suite at a stack you started yourself). */

function required(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(
      `${name} is not set. Start the suite through e2e/run.ps1 (Windows) or e2e/run.sh (Linux/macOS); they create the stack and export it.`,
    );
  }
  return value;
}

/** Origin of the web container (nginx). "localhost" on purpose: the API rejects IP-literal Host headers. */
export const BASE_URL = process.env.E2E_BASE_URL ?? "http://localhost:8181";

/** The API is only ever reached through nginx, exactly like a browser does. */
export const API_URL = `${BASE_URL}/api/v1`;

/** "main" = registration disabled (production default); "registration-open" = the variant with public sign-up. */
export const PHASE = process.env.E2E_PHASE ?? "main";

export function platformAdmin(): { email: string; password: string } {
  return {
    email: required("E2E_PLATFORM_ADMIN_EMAIL"),
    password: required("E2E_PLATFORM_ADMIN_PASSWORD"),
  };
}
