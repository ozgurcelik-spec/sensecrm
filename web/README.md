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
  components/            guards (protected-route, permission-guard, no-access), shell/, users/, roles/, audit/, crm/
  config/navigation.ts   left navigation and settings items with their required permissions
  hooks/                 usePermission, React Query hooks per resource, toast, language switch, useListParams
  layouts/app-layout.tsx top bar (org switcher, language, user menu) and module navigation
  lib/                   api-client (axios, refresh-on-401), api-error, dates, locale, theme, format, board
  pages/                 auth/, home, account, audit-log, crm/ (sales module pages), settings/{organization,users,roles,pipelines}
  services/              typed API calls (auth, me, organization, roles, accounts, contacts, leads, deals, pipelines, audit)
  store/                 zustand auth store (tokens + /me) and UI store
  test/crm.tsx           test helpers for the mocked API (route table, ProblemDetails errors, permission fixtures)
```

## Sales core (Milestone 2)

HTTP contract: `docs/plan/m2-api-kontrat.md`. Screens: Leads, Contacts, Accounts, Deals (`/app/<module>` list, `/app/<module>/:id` detail) and Settings > Pipelines.

- **List pages** share `hooks/use-list-params.ts` and `components/crm/data-table.tsx`: server-side paging (`page`, `pageSize`), debounced search (`q`), sortable columns (`sort=field` / `sort=-field`) and per-resource filters. All of it lives in the URL query string, so lists can be shared and survive reloads. The filter key arrays passed to `useListParams` must be module-level constants.
- **Forms** are Mantine modals with react-hook-form + zod (the repo's existing stack). Server `errors` are mapped onto fields (`applyValidationErrors`, dotted paths such as `billingAddress.city` supported); anything else goes through `toastApiError`, which translates `code` via `common:errors.<code>`. Mount a form dialog only while it is open; it initialises its state on mount.
- **Pickers**: owner from `/organization/members`; account with server-side search (`GET /accounts?q=`), so large tenants never load the whole list.
- **Detail pages** (`components/crm/record-detail-shell.tsx`): info panel on the left, tabs General / Related / Audit (`?tab=`), audit from `GET /audit?entityType&entityId`.
- **Lead conversion** (`lead-convert-dialog.tsx`): new or existing account (a same-named account is suggested), optional deal, then navigates to the deal (or the account).
- **Deals** switch between a kanban board and a list (`?view=list`) with a pipeline selector. The board uses `@dnd-kit/core`: drag with mouse/touch, or keyboard (Space on the handle, Left/Right to change column, Space to drop), or the per-card "move to" menu. The move is optimistic and rolled back on error; a lost stage asks for the lost reason first.
- **Write actions are permission-gated** with `useCrmPermissions()` (`crm.<resource>.write`; converting needs leads + accounts + contacts write; pipeline editing needs `org.settings.manage`). The server enforces them as well.
- **Tests** mock the axios client (`vi.mock("@/lib/api-client")`) and use `installApi` from `src/test/crm.tsx`.

## Auth flow

- Tokens are stored in localStorage (`auth_token`, `refresh_token`). The `/me` profile is persisted by the auth store.
- On a 401 from an authenticated request, the api-client refreshes once. Concurrent requests share the same refresh. It then retries the request. If the refresh fails, it clears the session and redirects to `/login`.
- API errors are ProblemDetails. The `code` is translated through `common:errors.<code>`, with the server `title` as the fallback.
- Permission checks in the UI use `usePermission(key)` and `<PermissionGuard>`. The server enforces every permission as well.
