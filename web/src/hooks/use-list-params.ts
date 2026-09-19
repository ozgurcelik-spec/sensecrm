import { useCallback, useMemo } from "react";
import { useSearchParams } from "react-router";

export const DEFAULT_PAGE_SIZE = 25;
export const PAGE_SIZE_OPTIONS = ["10", "25", "50", "100"] as const;

export interface SortState {
  field: string;
  descending: boolean;
}

/** `name` -> ascending, `-name` -> descending (the contract's `sort` value). */
export function parseSort(value: string | null | undefined): SortState | null {
  if (!value) return null;
  return value.startsWith("-")
    ? { field: value.slice(1), descending: true }
    : { field: value, descending: false };
}

export function formatSort(sort: SortState | null): string | undefined {
  return sort ? `${sort.descending ? "-" : ""}${sort.field}` : undefined;
}

function positiveInt(value: string | null, fallback: number): number {
  const parsed = Number.parseInt(value ?? "", 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

export interface ListParams<F extends string> {
  page: number;
  pageSize: number;
  q: string;
  sort: SortState | null;
  filters: Record<F, string>;
  /** Params for the list endpoint (empty values omitted by the service layer). */
  query: { page: number; pageSize: number; q?: string; sort?: string } & Partial<Record<F, string>>;
  setPage: (page: number) => void;
  setPageSize: (pageSize: number) => void;
  setQ: (q: string) => void;
  /** Click on a sortable column: ascending, then descending, then back to the default order. */
  toggleSort: (field: string) => void;
  setFilter: (key: F, value: string | null) => void;
  clearFilters: () => void;
  hasActiveFilters: boolean;
}

/**
 * List page state kept in the URL (`?page=2&pageSize=50&q=acme&sort=-name&ownerUserId=...`) so a
 * list can be shared, bookmarked and survives a reload / back navigation. Any change other than the
 * page itself resets to page 1. Defaults (page 1, default page size) are omitted from the URL.
 */
export function useListParams<F extends string>(
  filterKeys: readonly F[],
  options: { defaultPageSize?: number } = {}
): ListParams<F> {
  const defaultPageSize = options.defaultPageSize ?? DEFAULT_PAGE_SIZE;
  const [searchParams, setSearchParams] = useSearchParams();
  const raw = searchParams.toString();

  const state = useMemo(() => {
    const params = new URLSearchParams(raw);
    const filters = {} as Record<F, string>;
    for (const key of filterKeys) filters[key] = params.get(key) ?? "";
    return {
      page: positiveInt(params.get("page"), 1),
      pageSize: positiveInt(params.get("pageSize"), defaultPageSize),
      q: params.get("q") ?? "",
      sort: parseSort(params.get("sort")),
      filters,
    };
  }, [raw, filterKeys, defaultPageSize]);

  const update = useCallback(
    (mutate: (params: URLSearchParams) => void, resetPage = true) => {
      setSearchParams(
        (previous) => {
          const next = new URLSearchParams(previous);
          mutate(next);
          if (resetPage) next.delete("page");
          return next;
        },
        { replace: true }
      );
    },
    [setSearchParams]
  );

  const setPage = useCallback(
    (page: number) =>
      update((p) => (page <= 1 ? p.delete("page") : p.set("page", String(page))), false),
    [update]
  );

  const setPageSize = useCallback(
    (pageSize: number) =>
      update((p) =>
        pageSize === defaultPageSize ? p.delete("pageSize") : p.set("pageSize", String(pageSize))
      ),
    [update, defaultPageSize]
  );

  const setQ = useCallback(
    (q: string) => update((p) => (q ? p.set("q", q) : p.delete("q"))),
    [update]
  );

  const toggleSort = useCallback(
    (field: string) =>
      update((p) => {
        const current = parseSort(p.get("sort"));
        if (!current || current.field !== field) p.set("sort", field);
        else if (!current.descending) p.set("sort", `-${field}`);
        else p.delete("sort");
      }),
    [update]
  );

  const setFilter = useCallback(
    (key: F, value: string | null) => update((p) => (value ? p.set(key, value) : p.delete(key))),
    [update]
  );

  const clearFilters = useCallback(
    () =>
      update((p) => {
        for (const key of filterKeys) p.delete(key);
        p.delete("q");
      }),
    [update, filterKeys]
  );

  const query = useMemo(() => {
    const result: Record<string, string | number | undefined> = {
      page: state.page,
      pageSize: state.pageSize,
      q: state.q || undefined,
      sort: formatSort(state.sort),
    };
    for (const key of filterKeys) result[key] = state.filters[key] || undefined;
    return result as ListParams<F>["query"];
  }, [state, filterKeys]);

  const hasActiveFilters = filterKeys.some((key) => !!state.filters[key]) || !!state.q;

  return {
    ...state,
    query,
    setPage,
    setPageSize,
    setQ,
    toggleSort,
    setFilter,
    clearFilters,
    hasActiveFilters,
  };
}
