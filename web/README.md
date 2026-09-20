# Sense CRM Web

React frontend for Sense CRM, a multi-tenant SaaS CRM. Stack and folder layout follow the sibling `senseik` web app.

## Stack

- React 19, Vite 8, TypeScript (`tsc` is TypeScript 7 via `@typescript/native`; `typescript` is aliased to TS 6 for ESLint tooling)
- Mantine 9 for components, Tailwind CSS v4 for layout utilities only; `@mantine/charts` (recharts) for charts
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
  components/            guards (protected-route, permission-guard, no-access), shell/, users/, roles/, audit/, crm/, activities/, dashboard/, reports/, workflows/, marketing/
  config/navigation.ts   left navigation and settings items with their required permissions
  hooks/                 usePermission, React Query hooks per resource, toast, language switch, useListParams, use-platform / use-subscription / use-onboarding / use-module-enabled (M7)
  layouts/app-layout.tsx top bar (org switcher, language, user menu) and module navigation
  lib/                   api-client (axios, refresh-on-401), api-error, dates, locale, theme, format, board, zoned-time, report-range, csv
  pages/                 auth/, home, account, audit-log, approvals, crm/ (sales module pages), settings/{organization,users,roles,pipelines,workflows}
  services/              typed API calls (auth, me, organization, roles, accounts, contacts, leads, deals, pipelines, audit, activities, reports, workflows, approvals)
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

## Activities and reports (Milestone 3)

HTTP contract: `docs/plan/m3-aktivite-rapor.md`. Screens: Activities (`/app/activities`), the "Aktiviteler" tab on account / contact / lead / deal details, dashboard widgets on the home page and Reports (`/app/reports`).

- **Activities list** (`pages/crm/activities.tsx`): type tabs (`?type=`) and quick filters (Bugün `?due=today`, Geciken `?overdue=true`, Bana atanan `?assignedUserId=<me>`, Tümü clears them) sit in the URL with the other list params. Use `params.setFilters({...})` (not consecutive `setFilter` calls) when a click changes several filters; react-router does not queue functional `setSearchParams` updates.
- **Time zone**: "today" bounds, `datetime-local` inputs and report ranges use the organization's time zone (`lib/zoned-time.ts`), not the browser's. `dueFrom` / `dueTo` are sent as UTC ISO instants (`dueTo` = last millisecond of the day).
- **Form** (`components/activities/activity-form-dialog.tsx`): fields follow the type (task: due; call/meeting: start/end, end not before start; note: no date, priority or status). The type of an existing activity is fixed. Server field errors and `activity.invalid_range` / `activity.related_not_found` / `owner.not_member` are put on their fields. The related record is a type select plus a server-side search of that module's list endpoint (only modules the user may read).
- **Complete / reopen** (`useSetActivityStatus`): optimistic update of every cached list, rolled back with an error toast on failure; lists, summary, audit and the activities report are refetched afterwards. Notes have no such action.
- **Detail tab** (`components/activities/record-activities-tab.tsx`): the record's activities plus a quick-add form (type, subject, due) whose related record is fixed. Shown with `crm.activities.read`; writing needs `crm.activities.write`.
- **Dashboard**: "Benim işlerim" needs `crm.activities.read`; funnel, won/lost (last 6 months) and lead source widgets need `crm.reports.read`. The chart widgets are a lazy chunk (recharts), only requested for users who may read reports.
- **Reports**: range preset (`?range=thisMonth|last3Months|last12Months|custom`, custom `?from=&to=`), tab (`?tab=`), pipeline (`?pipelineId=`) and grouping (`?groupBy=week`) live in the URL. Only the active tab is requested; the funnel ignores the range. CSV is built in the browser (`lib/csv.ts`): UTF-8 BOM, `;` separator and decimal comma for Turkish (`,` and `.` for English), quoted/escaped cells, formula-looking text prefixed with `'`.
- **Tests** that render charts mock `@mantine/charts` with `src/test/charts.tsx` (recharts measures 0x0 in jsdom).

## Workflows and approvals (Milestone 4)

HTTP contract: `docs/plan/m4-workflow.md`. Screens: Settings > İş akışları (`/app/settings/workflows`, `org.workflows.manage`) and Onaylarım (`/app/approvals`). New i18n namespace `workflows`; new permissions `org.workflows.manage` and `crm.approvals.decide` (labels in `users.json`, listed in the role editor from `GET /permissions`).

- **Rules tab**: table with kind badge, parameter summary and an enabled switch (optimistic, rolled back with an error toast on failure). The create / edit dialog (`components/workflows/rule-form-dialog.tsx`) adapts to the kind: lead assignment = sources multi-select (empty = all), assignee role (`GET /organization/roles`), follow-up hours 1-720 (default 24); deal approval = minimum amount > 0 and approver role. The kind of an existing rule is fixed. Server field errors (`params.<field>` paths) and `workflow.role_not_found` are put on their fields.
- **Executions tab**: status, rule and date-range filters plus paging live in the URL (`?tab=executions&status=&ruleId=&from=YYYY-MM-DD&to=&page=`); `from`/`to` are sent as UTC instants of the organization's day bounds. `?execution=<id>` opens the detail drawer (step timeline, approvals, error, Terminate for running, Retry for failed, both after a confirmation).
- **Onaylarım**: pending and history tabs (`?tab=history&status=`, always `mine=true`); approving / rejecting needs `crm.approvals.decide`; a rejection needs a comment (client check and server `comment` error); `approval.already_decided` shows a friendly message and refreshes the lists.
- **Top bar badge** (`components/shell/approvals-bell.tsx`): `GET /approvals/summary` polled every 60 s while the tab is visible, refetched when the tab becomes visible again and after every decision. The sidebar entry is visible to `crm.approvals.decide` or while the caller has pending approvals.
- **Workflow strip** (`components/workflows/workflow-status-strip.tsx`): on the lead and deal "Genel" tab, "İş akışı: <rule> — çalışıyor/tamamlandı/hata" from `GET /workflows/executions?subjectType=- **Not built**: the workflow status strip on the deal / lead "Genel" tab. The contract has no endpoint that lists executions by subject (`GET /workflows/executions` filters only by status, rule and date), so it would need a new backend filter.subjectId=&page=1&pageSize=1`, only with `org.workflows.manage`; it links to the execution drawer and renders nothing while loading, when empty and on any error.

## Deployment (Milestone 5)

- **Image**: `web/Dockerfile` (build context = repo root): `node:24-alpine` builds with `VITE_API_BASE_URL=/api/v1` (same origin, no host name baked in), `nginxinc/nginx-unprivileged` serves `dist`. `web/nginx/default.conf.template` is rendered at container start (`API_UPSTREAM`, `DNS_RESOLVER`, `TRUSTED_PROXY_CIDR`): SPA history fallback, gzip, `/assets` cached for a year (`immutable`), `index.html` and `/locales` revalidated, security headers with a strict CSP (`default-src 'self'`; inline styles allowed for Mantine), `/api/*` reverse proxy, `/healthz` for the container health check. TLS is terminated outside (see `deploy/tls/`).
- **Sign-up link**: `GET /auth/config` (`hooks/use-auth-config.ts`) decides whether the login page shows "Ücretsiz kaydolun" and whether `/signup` renders (otherwise it redirects to `/login`). Public registration is disabled by default in Production (`Registration:Mode`); anything but an explicit `signupEnabled: true` counts as closed. Organizations are created by the platform admin (`POST /platform/organizations`), see `docs/operations/runbook.md`.
- Full stack, secrets, backup and upgrade procedure: `docs/operations/runbook.md`, `deploy/docker-compose.prod.yml`.

## Marketing (Milestone 6C)

HTTP contract: `docs/plan/m6c-pazarlama.md`. Screens: Campaigns (`/app/campaigns`, `/app/campaigns/:id`, `crm.campaigns.read`), the "Kampanyalar" tab on lead and contact details, the "Pazarlama" tab of Reports and the "Kampanya özeti" dashboard card. New i18n namespace `campaigns` (also holds the report / dashboard / audit-field strings); new permissions `crm.campaigns.read` / `crm.campaigns.write` (labels in `users.json`).

- **List** (`pages/crm/campaigns.tsx`): type and status are multi-selects sent as comma separated values (`?status=planned,active`); filters, search, sort and page live in the URL like the other lists.
- **Detail** (`pages/crm/campaign-detail.tsx`): tabs General (metric cards from `GET /campaigns/{id}/metrics`, computed on the server) / Members / Audit. The status menu (`components/marketing/campaign-status-menu.tsx`) offers only the targets of the transition table in `types/campaigns.ts`; a 409 becomes an error toast. Editing never sends `status`.
- **Members tab**: row status selector (`converted` is shown locked and can never be chosen), bulk status change and removal with a confirmation, "Add members" (type select + server-side search, at most 500 ids per call). A completed or cancelled campaign disables "Add members" (status change and removal stay open). Membership ids (`CampaignMember.id`) are used for status and removal, record ids (`memberId`) for adding.
- **Bulk "Kampanyaya ekle"**: `DataTable` has an opt-in `selection` prop (checkbox column; without it nothing changes). Leads and Contacts enable it with `crm.campaigns.write`. `hooks/use-row-selection.ts` binds the selection to the current page / filters / sort, so it reads as empty after any change. The add dialog lists only planned and active campaigns.
- **Report and dashboard**: the Marketing report tab (`components/reports/marketing-report-tab.tsx`, `crm.reports.read`) follows the shared range picker; amounts are summed across currencies (server limitation). The dashboard card needs `crm.reports.read` and `crm.campaigns.read` and uses the report's default range.
- **Audit**: the campaign Audit tab reads `entityType=Campaign`; field labels that `crm:auditFields` lacks fall back to `campaigns:auditFields`.

## Commerce (Milestone 6A)

HTTP contract and rules: `docs/plan/m6a-ticaret.md`. Screens: Ürünler (`/app/products`), Teklifler (`/app/quotes`, `/new`, `/:id`, `/:id/edit`), Siparişler (`/app/orders`, `/new`, `/:id`, `/:id/edit`), the "Teklifler" / "Siparişler" tabs on account and deal details, "Teklif oluştur" on both, and the "Ticaret" tab of Reports. New i18n namespace `commerce`; new permissions `crm.products|quotes|orders.read|write` (labels in `users.json`, error codes under `common:errors.{commerce,product,quote,order}`).

- **Pages** live in `pages/commerce/`, parts in `components/commerce/`, services `products|quotes|orders|commerce-reports.service.ts`, hooks `use-products|quotes|orders|commerce-report`. Lists reuse `useListParams` + `DataTable` (filters in the URL); "accepted, not yet converted" sets `status=accepted&converted=false`.
- **Line item grid** (`components/commerce/line-items-grid.tsx`) is shared by the quote and the order editor (`document-editor.tsx`): product picker (server-side search of active products, other-currency products disabled, fills description / unit price / tax rate), add / remove / move rows with buttons, per-row totals and a live totals card. Numeric cells keep the raw input (a number or text like `2.`), so typing decimals never clears a field.
- **Totals preview** (`lib/commerce-totals.ts`): the plan's algorithm with BigInt scaled integers (quantity and price 1e4, percentages 1e2, half-up rounding per line, totals are sums of rounded lines). It is display only; the editor sends the contract body without any computed field and the detail pages show the server's values. `lib/commerce-totals.test.ts` uses the plan's reference vectors (same table as the backend).
- **Server errors**: `errors["lines[i].field"]` land on the grid cell (`lib/commerce-lines.ts`), header fields on their inputs, other messages in an alert, coded errors (`commerce.*`, `quote.*`, `order.*`) through `toastApiError`.
- **Detail actions** (`lib/commerce-actions.ts`) are the status machine crossed with permissions: quote draft (edit, send, delete), sent (accept, reject, extend, revert), expired (extend, revert, reject), rejected (revert), accepted (convert with `crm.orders.write`, or the order link once converted); order draft (edit, confirm, cancel, delete), confirmed (fulfill, cancel). Only drafts have an edit route; other statuses redirect to the detail page. The editors need `crm.quotes.write` / `crm.orders.write` (read-only users get the no-access page).
- **Without `crm.products.read`** the product column is off and lines are typed by hand; without `crm.accounts.read` the account can only come from a "Teklif oluştur" prefill (shown read-only). The prefill (`?accountId&contactId&dealId`) carries only ids; names, currency and the subject come from the deal / account lookups.
- Test timeout is 20 s (`vitest.config.ts`): form tests are slow when the machine is loaded.

## Service: cases and SLA (Milestone 6B)

HTTP contract: `docs/plan/m6b-servis.md`. Screens: Talepler (`/app/cases`, `/app/cases/:id`, `crm.cases.read`), Settings > SLA politikaları (`/app/settings/sla`, `org.settings.manage`), the "Talepler" tab and "Talep aç" button on account / contact details, the "Servis" reports tab and the home "Servis" card. New i18n namespace `service`; new permissions `crm.cases.read` / `crm.cases.write` (labels in `users.json`); `case.*` and `general.concurrency_conflict` error texts in `common.json`.

- **List** (`pages/crm/cases.tsx`): all state is in the URL (`?page&pageSize&q&sort&status&priority&channel&assignedUserId&unassigned&slaState&accountId&contactId`); `status` / `priority` are comma separated multi values (`status=new,open,pending`), the request always sends `sort` (`-createdAt` by default). Quick chips Açık / Bana atanan / Atanmamış / SLA aşıldı / Tümü set several filters at once with `setFilters`; assignee and "unassigned" exclude each other (the server rejects both together).
- **SLA badge** (`components/service/case-badges.tsx`): green ok, yellow at risk, red breached, from the server's `slaState`; the tooltip shows both targets with the time left / past. A resolved or closed case only shows a badge when its SLA was breached.
- **Detail** (`pages/crm/case-detail.tsx`): timeline (comments + events, newest first, `pageSize=50`, "Daha fazla göster") and reply box on the left, info panel on the right (`RecordDetailShell panelSide="right"`). The reply type (public reply / internal note) has **no default**; Send stays disabled until one is chosen and it is reset after sending. Internal notes have a yellow background and a "Dahili" label. A closed case shows a banner instead of the box.
- **Actions** (`components/service/case-actions.tsx`, `crm.cases.write`): the Durum menu is built from `lib/case.ts` `CASE_TRANSITIONS` (the §3.2 table): resolving, and closing an unresolved case, ask for a resolution note first (an empty note is never sent); a closed case older than 14 days has no Reopen (the server decides: `case.reopen_window_expired` is toasted). Edit, priority and assignee are disabled outside new / open / pending. Any 409 (`general.concurrency_conflict`, `case.not_active`, ...) toasts the translated error and reloads the case and its timeline.
- **Create dialog** (`case-form-dialog.tsx`): picking a contact fills an empty account, picking an account narrows the contacts (`GET /contacts?accountId=`); `fixedAccount` / `fixedContact` preset and lock the record for "Talep aç". Success opens the new case.
- **Tests** mock the axios client and use `installApi`; `src/test/service.ts` has the case / timeline fixtures. `service-locales.test.ts` keeps `service.json` in step between tr and en.

## SaaS readiness (Milestone 7)

HTTP contract: `docs/plan/m7-saas-hazirlik.md`. Screens: the platform console (`/app/platform/organizations`, `/:tenantId`, `plans`, `audit`; platform admins only), Settings > Plan ve kullanım (`/app/settings/plan`, `org.settings.manage`), global banners, the blocked screen and the home onboarding card. New i18n namespaces `platform` and `subscription` (the plan / tenant-state error texts live in `subscription:errors.*`, the `platform.*` lifecycle codes in `platform:errors.platform.*`; both are looked up by `getApiErrorMessage` with the server's `args`).

- **Platform console** (`pages/platform/*`, `components/platform/*`): the route group is wrapped in `PlatformGuard` (`me.user.isPlatformAdmin`, everyone else gets `NoAccess`; tenant permissions never open it) and the "Platform" navigation group (`PLATFORM_ITEMS`, `platformAdminOnly`) only shows for platform admins. The list uses `useListParams` (`?q&status&planCode&source&sort&page&pageSize`). The detail page offers only the actions valid for the state (`lib/platform.ts` `organizationActions`); the system organization gets every action disabled with an explanation. Plan / trial / overrides are edited in one dialog (`PUT .../subscription`, full replacement); `errors.*` paths, `platform.plan_not_found` and the `overLimit` report are mapped in place. Deletion requires typing the organization name and a 7-90 day retention. The usage tab lazy-loads recharts; the CSV export is fetched as a blob and saved by the browser.
- **Plan state** comes from `GET /me` `subscription` (`me.subscription`; absent = everything on, full access). `lib/entitlements.ts` is the single place that maps it: `usePermission` / `PermissionGuard` / `authStore.hasPermission` return false for `.write` / `.decide` keys while `accessLevel` is `readOnly`, and for every key of a disabled module (`permissionModule`: campaigns -> marketing, products / quotes / orders -> commerce, cases -> service, workflows / approvals -> workflows), so buttons, tabs and widgets disappear and the queries behind them are never sent. `NavItem.module` hides menu entries; `RequirePermission` and `ModuleGuard` answer routes of a disabled module with the "module disabled" page; the top-bar approvals request is skipped without the workflows module. `accessLevel: none` replaces the whole shell with `BlockedScreen` (only `/me` is requested).
- **Banners / refresh**: `SubscriptionBanner` (trial <= 7 days, orange at <= 2, expired, suspended). `useSubscriptionSync` re-reads `/me` every 5 minutes; a 402/403 `tenant.suspended` / `plan.*` answer also re-reads it (`setPlanStateErrorHandler`, at most every 10 s). `plan.limit_exceeded` toasts "Plan limitine ulaşıldı: Kullanıcı 5/5" with a link to the plan page (the dialog that caused it stays open).
- **Onboarding** (`components/subscription/onboarding-card.tsx`): home page, `org.settings.manage`, full access only; hidden when dismissed / complete; the workflow step is dropped without the workflows module; errors render nothing.
- **Tests**: `platform-app.test.tsx` (route guarding, nav hiding, module-disabled pages, blocked screen, banners), `pages/platform/*.test.tsx`, `components/subscription/subscription.test.tsx`, `pages/settings/plan-usage.test.tsx`, `lib/entitlements.test.ts`, `platform-locales.test.ts`; fixtures in `src/test/platform.ts`. Permission totals are never pinned (`Object.values(PERMISSIONS)`).

## Auth flow

- Tokens are stored in localStorage (`auth_token`, `refresh_token`). The `/me` profile is persisted by the auth store.
- On a 401 from an authenticated request, the api-client refreshes once. Concurrent requests share the same refresh. It then retries the request. If the refresh fails, it clears the session and redirects to `/login`.
- API errors are ProblemDetails. The `code` is translated through `common:errors.<code>`, with the server `title` as the fallback.
- Permission checks in the UI use `usePermission(key)` and `<PermissionGuard>`. The server enforces every permission as well.

## Security hardening (C-SEC)

New i18n namespace `security` (screens, policy hints, C-SEC error codes via `security:errors.<code>`, looked up after `common:errors.<code>` by `getApiErrorMessage`).

- **Password policy** (`lib/password-policy.ts`): 10-128 characters and no e-mail local part (3+ characters), mirrored client-side on sign-up and change-password; the server additionally rejects common passwords and its field message is shown as-is. Passwords are never trimmed.
- **Forced change**: `me.mustChangePassword` / login / refresh tokens set `useAuthStore().mustChangePassword`; a 403 `auth.password_change_required` from any request does the same (`setPasswordChangeRequiredHandler` in `lib/api-client.ts`). `ProtectedRoute` then redirects to the full-page `/change-password` (no shell); after success (`POST /me/password`, new tokens stored) it continues to `/app`. The same `components/security/change-password-form.tsx` is on the Profile page. `POST /me/password` uses `passthroughUnauthorized` so a wrong current password (401) is a field error, not a session end.
- **Members**: create takes `{ email, displayName, roleId }` only. A new account's `temporaryPassword` is shown once in a copyable dialog; `status: "pending"` (existing account invited) shows an info toast. Pending members render read-only with a "Davet bekliyor" badge and are never offered as record owners.
- **Invitations**: `components/shell/invitations-bell.tsx` next to the approvals bell (`GET /me/invitations`, polled every 60 s while visible); the dialog accepts/declines and, after accepting, refetches `/me` and offers "Şimdi geç" to switch organization.
- **Website**: only absolute `http(s)://` URLs are valid in the account form and rendered as links (`lib/url.ts`, `components/crm/website-link.tsx`); anything else is inert text.
