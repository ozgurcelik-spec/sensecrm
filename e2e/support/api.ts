/**
 * Thin HTTP client for the CRM API (through nginx, like the browser). Tests use it to seed data and to change state that is not the
 * subject of the test, so the UI journeys stay short and independent. It logs in lazily and once, and logs in again on a 401.
 */
import { API_URL } from "./env.ts";

export interface Credentials {
  email: string;
  password: string;
}

export interface Tokens {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  mustChangePassword?: boolean;
}

export class ApiError extends Error {
  readonly method: string;
  readonly path: string;
  readonly status: number;
  readonly body: unknown;

  constructor(method: string, path: string, status: number, body: unknown) {
    super(`${method} ${path} -> ${status} ${typeof body === "string" ? body : JSON.stringify(body)}`);
    this.name = "ApiError";
    this.method = method;
    this.path = path;
    this.status = status;
    this.body = body;
  }
}

async function send(method: string, path: string, body?: unknown, token?: string): Promise<Response> {
  const headers: Record<string, string> = { Accept: "application/json" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (token) headers.Authorization = `Bearer ${token}`;
  return fetch(`${API_URL}${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

async function parse(response: Response): Promise<unknown> {
  const text = await response.text();
  if (!text) return undefined;
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

export async function rawLogin(credentials: Credentials): Promise<Tokens> {
  const response = await send("POST", "/auth/login", credentials);
  const body = await parse(response);
  if (!response.ok) throw new ApiError("POST", "/auth/login", response.status, body);
  return body as Tokens;
}

export class ApiClient {
  private tokens?: Tokens;
  private credentials: Credentials;

  constructor(credentials: Credentials) {
    this.credentials = credentials;
  }

  get email(): string {
    return this.credentials.email;
  }

  get password(): string {
    return this.credentials.password;
  }

  /** Use after the password of this user changed. */
  setPassword(password: string, tokens?: Tokens): void {
    this.credentials = { ...this.credentials, password };
    this.tokens = tokens;
  }

  async login(): Promise<Tokens> {
    this.tokens = await rawLogin(this.credentials);
    return this.tokens;
  }

  async session(): Promise<Tokens> {
    return this.tokens ?? (await this.login());
  }

  async request<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
    let tokens = await this.session();
    let response = await send(method, path, body, tokens.accessToken);
    if (response.status === 401) {
      tokens = await this.login();
      response = await send(method, path, body, tokens.accessToken);
    }
    const payload = await parse(response);
    if (!response.ok) throw new ApiError(method, path, response.status, payload);
    return payload as T;
  }

  get = <T = unknown>(path: string) => this.request<T>("GET", path);
  post = <T = unknown>(path: string, body?: unknown) => this.request<T>("POST", path, body ?? {});
  put = <T = unknown>(path: string, body?: unknown) => this.request<T>("PUT", path, body);
  patch = <T = unknown>(path: string, body?: unknown) => this.request<T>("PATCH", path, body);
  del = <T = unknown>(path: string) => this.request<T>("DELETE", path);
}

/** Status of a request without throwing (for negative checks such as "the API must refuse this"). */
export async function statusOf(method: string, path: string, token?: string, body?: unknown): Promise<number> {
  return (await send(method, path, body, token)).status;
}
