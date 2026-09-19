import { useCallback, useMemo, useState } from "react";

export interface RowSelection {
  selected: ReadonlySet<string>;
  /** Replaces the selection. */
  onChange: (keys: ReadonlySet<string>) => void;
  clear: () => void;
}

/**
 * Row selection of a list page. The selection belongs to `contextKey` (the current page, filters
 * and sort): as soon as the key changes the selection reads as empty, so a page or filter change
 * clears it without an effect.
 */
export function useRowSelection(contextKey: string): RowSelection {
  const [state, setState] = useState<{ key: string; keys: ReadonlySet<string> }>({
    key: contextKey,
    keys: new Set(),
  });
  const selected = useMemo<ReadonlySet<string>>(
    () => (state.key === contextKey ? state.keys : new Set()),
    [state, contextKey]
  );
  const onChange = useCallback(
    (keys: ReadonlySet<string>) => setState({ key: contextKey, keys }),
    [contextKey]
  );
  const clear = useCallback(() => setState({ key: contextKey, keys: new Set() }), [contextKey]);
  return { selected, onChange, clear };
}
