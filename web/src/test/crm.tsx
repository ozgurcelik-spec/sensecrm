/**
 * Helpers for the Milestone 2 component tests: permission fixtures, ProblemDetails errors, a tiny
 * route table for the mocked axios client and a location probe.
 */
import { AxiosError, AxiosHeaders } from "axios";
import { useLocation } from "react-router";
import { act } from "@testing-library/react";
import type { Mock } from "vitest";
import { useAuthStore } from "@/store/auth.store";
import type { Me } from "@/types";

export const ME_ID = "user-1";

export function meWith(permissions: string[]): Me {
  return {
    user: { id: ME_ID, email: "ada@example.com", displayName: "Ada", locale: "tr", isPlatformAdmin: false },
    organization: { id: "o1", name: "Acme", slug: "acme", defaultLocale: "tr", timeZone: "Europe/Istanbul" },
    role: { id: "r1", name: "Sales" },
    permissions,
    organizations: [{ id: "o1", name: "Acme", slug: "acme" }],
  };
}

export function setPermissions(permissions: string[]): void {
  act(() => useAuthStore.setState({ me: meWith(permissions) }));
}

export function clearSession(): void {
  act(() => useAuthStore.setState({ me: null }));
}

/** An axios error carrying a backend ProblemDetails body. */
export function problem(status: number, data: Record<string, unknown>): AxiosError {
  const headers = new AxiosHeaders();
  return new AxiosError("Request failed", "ERR_BAD_REQUEST", { headers }, undefined, {
    status,
    statusText: "",
    headers: {},
    config: { headers },
    data,
  });
}

export interface ApiRequest {
  url: string;
  params?: Record<string, unknown>;
  body?: unknown;
}

export type ApiHandler = (request: ApiRequest) => unknown;

/** The mocked axios client shape (`vi.mock("@/lib/api-client")`). */
export interface MockClient {
  get: Mock;
  post: Mock;
  put: Mock;
  patch: Mock;
  delete: Mock;
}

/**
 * Routes "METHOD /path" (path relative to the API base) to handlers. A handler's return value becomes
 * `response.data`; a returned/thrown Error rejects; unknown routes reject so missing mocks fail loudly.
 */
export function installApi(client: MockClient, routes: Record<string, ApiHandler>): void {
  const dispatch = (method: string) => async (url: string, second?: unknown, third?: unknown) => {
    const isBodyMethod = method === "POST" || method === "PUT" || method === "PATCH";
    const body = isBodyMethod ? second : undefined;
    const config = (isBodyMethod ? third : second) as { params?: Record<string, unknown> } | undefined;
    const handler = routes[`${method} ${url}`];
    if (!handler) throw new Error(`Unmocked API call: ${method} ${url}`);
    const result = await handler({ url, params: config?.params, body });
    if (result instanceof Error) throw result;
    return { data: result, status: 200 };
  };
  client.get.mockImplementation(dispatch("GET"));
  client.post.mockImplementation(dispatch("POST"));
  client.put.mockImplementation(dispatch("PUT"));
  client.patch.mockImplementation(dispatch("PATCH"));
  client.delete.mockImplementation(dispatch("DELETE"));
}

/** Renders the current router location so tests can assert URL-synced state and navigation. */
export function LocationDisplay() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname + location.search}</div>;
}

export function page<T>(items: T[], overrides: { page?: number; pageSize?: number; totalCount?: number } = {}) {
  return {
    items,
    page: overrides.page ?? 1,
    pageSize: overrides.pageSize ?? 25,
    totalCount: overrides.totalCount ?? items.length,
  };
}

export const MEMBERS = [
  { userId: ME_ID, email: "ada@example.com", displayName: "Ada Lovelace", roleId: "r1", roleName: "Sales", isActive: true, joinedAt: "2026-01-01T00:00:00Z" },
  { userId: "user-2", email: "grace@example.com", displayName: "Grace Hopper", roleId: "r1", roleName: "Sales", isActive: true, joinedAt: "2026-01-01T00:00:00Z" },
];
