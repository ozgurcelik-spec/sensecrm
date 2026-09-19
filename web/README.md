# Sense CRM Web

React frontend for Sense CRM, a multi-tenant SaaS CRM. Stack and folder layout follow the sibling `senseik` web app.

## Stack

- React 19, Vite 8, TypeScript (`tsc` is TypeScript 7 via `@typescript/native`; `typescript` is aliased to TS 6 for ESLint tooling)
- Mantine 9 for components, Tailwind CSS v4 for layout utilities only
- react-router, TanStack Query (server state), zustand (auth/UI state), axios (API client)
- react-hook-form + zod for forms
- i18next + react-i18next, with JSON namespaces in `public/locales/{tr,en}/*.json`. Turkish is the default language.
- lucide-react for icons
- Vitest + Testing Library for tests

## Commands

Run these from the repo root. It is a pnpm workspace; each script forwards to `web/`.

```bash
pnpm install        # install workspace dependencies (corepack pnpm install if pnpm is not on PATH)
pnpm dev            # Vite dev server on http://localhost:3000, with /api proxied to http://localhost:5080
pnpm build          # tsc --noEmit + vite build -> web/dist
pnpm type-check     # tsc --noEmit
pnpm lint           # eslint .
pnpm test           # vitest in watch mode; use `pnpm --filter web exec vitest run` for a single run
pnpm format         # prettier
```

The root scripts call `pnpm` directly, so `pnpm` must be on PATH (for example after `corepack enable`). Without it, call the package directly: `corepack pnpm --filter web <script>`.

Environment settings are in `.env.example`. `VITE_API_BASE_URL` defaults to `/api/v1`. `VITE_API_PROXY_TARGET` sets the dev proxy target.

## Layout

```
src/
  App.tsx                routes (/login, /signup, /app/...) with lazy pages
  main.tsx               providers (React Query, Mantine) and session handlers
  i18n.ts                i18next (http backend, TR default)
  components/            guards (protected-route, permission-guard, no-access), shell/, users/, roles/, audit/
  config/navigation.ts   left navigation and settings items with their required permissions
  hooks/                 usePermission, React Query hooks per resource, toast, language switch
  layouts/app-layout.tsx top bar (org switcher, language, user menu) and module navigation
  lib/                   api-client (axios, refresh-on-401), api-error, dates, locale, theme
  pages/                 auth/, home, account, audit-log, settings/{organization,users,roles}
  services/              typed API calls (auth, me, organization, roles)
  store/                 zustand auth store (tokens + /me) and UI store
```

## Auth flow

- Tokens are stored in localStorage (`auth_token`, `refresh_token`). The `/me` profile is persisted by the auth store.
- On a 401 from an authenticated request, the api-client refreshes once. Concurrent requests share the same refresh. It then retries the request. If the refresh fails, it clears the session and redirects to `/login`.
- API errors are ProblemDetails. The `code` is translated through `common:errors.<code>`, with the server `title` as the fallback.
- Permission checks in the UI use `usePermission(key)` and `<PermissionGuard>`. The server enforces every permission as well.
