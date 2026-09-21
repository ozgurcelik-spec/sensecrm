/**
 * Contract of the shared lookup ("... Seç") window of docs/plan/m9c-envanter.md: a source describes
 * how to search one entity (it wraps the existing paged list hook), the dialog renders the table.
 */
import type { ComponentType, ReactNode } from "react";

/** The search request follows the typing after this pause (ms). */
export const LOOKUP_DEBOUNCE_MS = 250;

export type LookupFilters = Record<string, string | number | boolean | undefined>;

export interface LookupColumn<T> {
  key: string;
  header: string;
  render(row: T): ReactNode;
  /** Server-side sort field; the header becomes a sort button when present. */
  sortField?: string;
}

export interface LookupSearchParams {
  q?: string;
  page: number;
  pageSize: number;
  sort?: string;
  filters?: LookupFilters;
}

export interface LookupSearchResult<T> {
  items: T[];
  totalCount: number;
  isFetching: boolean;
  isError: boolean;
}

export interface LookupSource<T> {
  /** React Query key prefix: a quick-create invalidates it so the new record shows up. */
  queryKey: string;
  /** A hook wrapping the entity's existing paged list hook. */
  useSearch(params: LookupSearchParams): LookupSearchResult<T>;
  getId(row: T): string;
  getLabel(row: T): string;
  columns: LookupColumn<T>[];
  defaultSort?: string;
}

export interface LookupCreateDialogProps<T> {
  opened: boolean;
  onClose(): void;
  /** What was typed in the search box: a starting value for the name. */
  initialName?: string;
  /** The dialog's filters as strings (for example `accountId`): records created here start with them. */
  presetFilters?: Record<string, string>;
  onCreated(row: T): void;
}

export interface LookupCreate<T> {
  /** Permission the "+ Yeni ..." button needs (write key of the entity). */
  permission: string;
  label: string;
  Dialog: ComponentType<LookupCreateDialogProps<T>>;
}

export interface LookupDialogProps<T> {
  opened: boolean;
  onClose(): void;
  title: string;
  source: LookupSource<T>;
  filters?: LookupFilters;
  selectedId?: string;
  onSelect(row: T): void;
  create?: LookupCreate<T>;
  /** A reason (shown as a hint) makes the row unselectable, for example a product in another currency. */
  disabledRow?(row: T): string | undefined;
  pageSize?: 10;
}

/** What a `LookupField` displays: the id and label of the picked record (need not be in the list). */
export interface LookupValue {
  id: string;
  label: string;
}
