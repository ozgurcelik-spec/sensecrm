import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { QueryClient } from "@tanstack/react-query";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test-utils";
import { clearSession, setPermissions } from "@/test/crm";
import { LookupDialog } from "./lookup-dialog";
import { LookupField } from "./lookup-field";
import type {
  LookupCreate,
  LookupCreateDialogProps,
  LookupSearchParams,
  LookupSource,
} from "./lookup-types";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

interface Row {
  id: string;
  name: string;
  currency: string;
}

const ROWS: Row[] = Array.from({ length: 23 }, (_, i) => ({
  id: `r${i + 1}`,
  name: `Kayıt ${String(i + 1).padStart(2, "0")}`,
  currency: i === 1 ? "USD" : "TRY",
}));

/** A source over a static list; every search request is recorded. */
function makeSource(rows: Row[] = ROWS, state: { isError?: boolean; isFetching?: boolean } = {}) {
  const calls: LookupSearchParams[] = [];
  const source: LookupSource<Row> = {
    queryKey: "testlookup",
    useSearch(params) {
      calls.push(params);
      const q = (params.q ?? "").toLowerCase();
      const filtered = rows.filter((row) => row.name.toLowerCase().includes(q));
      const sorted = params.sort?.startsWith("-")
        ? [...filtered].reverse()
        : filtered;
      const start = (params.page - 1) * params.pageSize;
      return {
        items: sorted.slice(start, start + params.pageSize),
        totalCount: sorted.length,
        isFetching: state.isFetching ?? false,
        isError: state.isError ?? false,
      };
    },
    getId: (row) => row.id,
    getLabel: (row) => row.name,
    defaultSort: "name",
    columns: [
      { key: "name", header: "Ad", sortField: "name", render: (row) => row.name },
      { key: "currency", header: "Para birimi", render: (row) => row.currency },
    ],
  };
  return { source, calls };
}

function Harness(props: {
  source: LookupSource<Row>;
  onSelect?: (row: Row) => void;
  onClose?: () => void;
  selectedId?: string;
  filters?: Record<string, string | undefined>;
  create?: LookupCreate<Row>;
  disabledRow?: (row: Row) => string | undefined;
}) {
  const [opened, setOpened] = useState(true);
  return (
    <LookupDialog<Row>
      opened={opened}
      onClose={() => {
        props.onClose?.();
        setOpened(false);
      }}
      title="Kayıt seç"
      source={props.source}
      selectedId={props.selectedId}
      filters={props.filters}
      create={props.create}
      disabledRow={props.disabledRow}
      onSelect={(row) => props.onSelect?.(row)}
    />
  );
}

const rowNames = () => screen.getAllByTestId("lookup-row").map((row) => within(row).getAllByRole("cell")[1]?.textContent);

describe("LookupDialog", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions([]);
  });
  afterEach(() => {
    clearSession();
    vi.restoreAllMocks();
  });

  it("opens with the search box focused and shows the first page of ten rows with the total", async () => {
    const { source } = makeSource();
    renderWithProviders(<Harness source={source} />);

    const search = await screen.findByRole("textbox", { name: "Ara" });
    expect(search).toHaveFocus();
    expect(screen.getAllByTestId("lookup-row")).toHaveLength(10);
    expect(rowNames()[0]).toBe("Kayıt 01");
    expect(screen.getByText("23 kayıt")).toBeInTheDocument();
    expect(screen.getByText("Sayfa 1 / 3")).toBeInTheDocument();
  });

  it("pages with Previous / Next and sends page and pageSize 10 to the source", async () => {
    const { source, calls } = makeSource();
    renderWithProviders(<Harness source={source} />);
    await screen.findByRole("textbox", { name: "Ara" });

    expect(screen.getByRole("button", { name: "Önceki" })).toBeDisabled();
    await userEvent.click(screen.getByRole("button", { name: "Sonraki" }));
    expect(rowNames()[0]).toBe("Kayıt 11");
    await userEvent.click(screen.getByRole("button", { name: "Sonraki" }));
    expect(screen.getAllByTestId("lookup-row")).toHaveLength(3);
    expect(screen.getByRole("button", { name: "Sonraki" })).toBeDisabled();
    expect(calls.at(-1)).toMatchObject({ page: 3, pageSize: 10 });
  });

  it("debounces the search: the request follows the typing after a pause, not each key", async () => {
    const { source, calls } = makeSource();
    renderWithProviders(<Harness source={source} />);
    const search = await screen.findByRole("textbox", { name: "Ara" });

    await userEvent.type(search, "kayıt 2");
    // While typing, no request carries a partial query...
    expect(calls.some((call) => call.q === "k" || call.q === "ka")).toBe(false);
    // ...and once the pause is over the whole text is searched, starting from page 1.
    await waitFor(() => expect(calls.at(-1)).toMatchObject({ q: "kayıt 2", page: 1 }));
    await waitFor(() => expect(screen.getAllByTestId("lookup-row")).toHaveLength(4));
  });

  it("sorts by a column header (ascending, descending, back to the default) and resets to page 1", async () => {
    const { source, calls } = makeSource();
    renderWithProviders(<Harness source={source} />);
    await screen.findByRole("textbox", { name: "Ara" });
    await userEvent.click(screen.getByRole("button", { name: "Sonraki" }));

    expect(calls.at(-1)?.sort).toBe("name");
    await userEvent.click(screen.getByRole("button", { name: "Ad sütununa göre sırala" }));
    // The default sort is already "name": a click on its header makes it descending.
    expect(calls.at(-1)).toMatchObject({ sort: "-name", page: 1 });
    await userEvent.click(screen.getByRole("button", { name: "Ad sütununa göre sırala" }));
    expect(calls.at(-1)?.sort).toBe("name");
  });

  it("selects on a row click, shows the selected radio and closes", async () => {
    const { source } = makeSource();
    const onSelect = vi.fn();
    const onClose = vi.fn();
    renderWithProviders(<Harness source={source} onSelect={onSelect} onClose={onClose} selectedId="r3" />);
    await screen.findByRole("textbox", { name: "Ara" });

    expect(screen.getByRole("radio", { name: "Kayıt 03" })).toBeChecked();
    expect(screen.getByRole("radio", { name: "Kayıt 04" })).not.toBeChecked();
    await userEvent.click(screen.getByText("Kayıt 04"));

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ id: "r4" }));
    expect(onClose).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByRole("textbox", { name: "Ara" })).not.toBeInTheDocument());
  });

  it("supports the keyboard: arrows move, Enter selects, Esc closes without selecting", async () => {
    const { source } = makeSource();
    const onSelect = vi.fn();
    const onClose = vi.fn();
    const first = renderWithProviders(<Harness source={source} onSelect={onSelect} onClose={onClose} />);
    const search = await screen.findByRole("textbox", { name: "Ara" });

    await userEvent.type(search, "{ArrowDown}{ArrowDown}{ArrowUp}{Enter}");
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ id: "r2" }));
    first.unmount();

    const second = renderWithProviders(<Harness source={source} onSelect={onSelect} onClose={onClose} />);
    await screen.findByRole("textbox", { name: "Ara" });
    onSelect.mockClear();
    onClose.mockClear();
    await userEvent.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalled();
    expect(onSelect).not.toHaveBeenCalled();
    second.unmount();
  });

  it("shows the reason of a disabled row and does not select it", async () => {
    const { source } = makeSource();
    const onSelect = vi.fn();
    renderWithProviders(
      <Harness
        source={source}
        onSelect={onSelect}
        disabledRow={(row) => (row.currency !== "TRY" ? "Para birimi uyuşmuyor" : undefined)}
      />
    );
    await screen.findByRole("textbox", { name: "Ara" });

    expect(screen.getByText("Para birimi uyuşmuyor")).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Kayıt 02" })).toBeDisabled();
    await userEvent.click(screen.getByText("Kayıt 02"));
    expect(onSelect).not.toHaveBeenCalled();
    await userEvent.click(screen.getByText("Kayıt 01"));
    expect(onSelect).toHaveBeenCalledTimes(1);
  });

  it("has empty, loading and error states", async () => {
    const empty = makeSource([]);
    const first = renderWithProviders(<Harness source={empty.source} />);
    expect(await screen.findByText("Kayıt bulunamadı")).toBeInTheDocument();
    first.unmount();

    const loading = makeSource([], { isFetching: true });
    const second = renderWithProviders(<Harness source={loading.source} />);
    expect(await screen.findByText("Yükleniyor...")).toBeInTheDocument();
    second.unmount();

    const failing = makeSource([], { isError: true });
    renderWithProviders(<Harness source={failing.source} />);
    expect(await screen.findByRole("alert")).toHaveTextContent("Liste yüklenemedi");
  });

  describe("quick create ('+ Yeni ...')", () => {
    function createDefinition() {
      // The dialog reports the props it renders with; tests read the latest ones.
      const capture = vi.fn<(dialogProps: LookupCreateDialogProps<Row>) => void>();
      const props = {
        get current(): LookupCreateDialogProps<Row> | undefined {
          return capture.mock.lastCall?.[0];
        },
      };
      function CreateDialog(dialogProps: LookupCreateDialogProps<Row>) {
        capture(dialogProps);
        return dialogProps.opened ? <div role="dialog" aria-label="Yeni kayıt formu">form</div> : null;
      }
      const create: LookupCreate<Row> = { permission: "crm.accounts.write", label: "+ Yeni kayıt", Dialog: CreateDialog };
      return { create, props };
    }

    it("is only offered with the write permission", async () => {
      const { source } = makeSource();
      const { create } = createDefinition();
      const first = renderWithProviders(<Harness source={source} create={create} />);
      await screen.findByRole("textbox", { name: "Ara" });
      expect(screen.queryByRole("button", { name: "+ Yeni kayıt" })).not.toBeInTheDocument();
      first.unmount();

      setPermissions(["crm.accounts.write"]);
      renderWithProviders(<Harness source={source} create={create} />);
      expect(await screen.findByRole("button", { name: "+ Yeni kayıt" })).toBeInTheDocument();
    });

    it("passes the typed text and the filters, then selects the created record, refreshes the list cache and closes", async () => {
      setPermissions(["crm.accounts.write"]);
      const invalidate = vi.spyOn(QueryClient.prototype, "invalidateQueries");
      const { source } = makeSource();
      const { create, props } = createDefinition();
      const onSelect = vi.fn();
      const onClose = vi.fn();
      renderWithProviders(
        <Harness source={source} create={create} onSelect={onSelect} onClose={onClose} filters={{ accountId: "a1", empty: undefined }} />
      );
      await userEvent.type(await screen.findByRole("textbox", { name: "Ara" }), "Yeni Ad");
      await userEvent.click(screen.getByRole("button", { name: "+ Yeni kayıt" }));

      expect(await screen.findByRole("dialog", { name: "Yeni kayıt formu" })).toBeInTheDocument();
      expect(props.current?.initialName).toBe("Yeni Ad");
      expect(props.current?.presetFilters).toEqual({ accountId: "a1" });

      const created: Row = { id: "new", name: "Yeni Ad", currency: "TRY" };
      props.current?.onCreated(created);

      await waitFor(() => expect(onSelect).toHaveBeenCalledWith(created));
      expect(onClose).toHaveBeenCalled();
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ["testlookup"] });
    });

    it("keeps the window and the selection unchanged when the creation is cancelled", async () => {
      setPermissions(["crm.accounts.write"]);
      const { source } = makeSource();
      const { create, props } = createDefinition();
      const onSelect = vi.fn();
      const onClose = vi.fn();
      renderWithProviders(<Harness source={source} create={create} onSelect={onSelect} onClose={onClose} />);
      await userEvent.click(await screen.findByRole("button", { name: "+ Yeni kayıt" }));
      expect(await screen.findByRole("dialog", { name: "Yeni kayıt formu" })).toBeInTheDocument();

      props.current?.onClose();

      await waitFor(() => expect(screen.queryByRole("dialog", { name: "Yeni kayıt formu" })).not.toBeInTheDocument());
      expect(screen.getByRole("textbox", { name: "Ara" })).toBeInTheDocument();
      expect(onSelect).not.toHaveBeenCalled();
      expect(onClose).not.toHaveBeenCalled();
    });
  });
});

describe("LookupField", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions([]);
  });
  afterEach(clearSession);

  function FieldHarness(props: {
    initial?: { id: string; label: string } | null;
    onChange?: (row: Row | null) => void;
    readOnly?: boolean;
    source: LookupSource<Row>;
  }) {
    const [value, setValue] = useState(props.initial ?? null);
    return (
      <LookupField<Row>
        label="Müşteri"
        title="Müşteri seç"
        source={props.source}
        value={value}
        clearable
        required
        readOnly={props.readOnly}
        onChange={(row, picked) => {
          setValue(picked);
          props.onChange?.(row);
        }}
      />
    );
  }

  it("shows the selected label even when it is not in the searched list", () => {
    const { source } = makeSource();
    renderWithProviders(<FieldHarness source={source} initial={{ id: "gone", label: "Silinmiş kayıt" }} />);
    expect(screen.getByRole("textbox", { name: "Müşteri" })).toHaveValue("Silinmiş kayıt");
  });

  it("opens the window on click, Enter and Space, picks a row and shows its label", async () => {
    const { source } = makeSource();
    const onChange = vi.fn();
    renderWithProviders(<FieldHarness source={source} onChange={onChange} />);
    const input = screen.getByRole("textbox", { name: "Müşteri" });

    await userEvent.click(input);
    expect(await screen.findByRole("dialog", { name: "Müşteri seç" })).toBeInTheDocument();
    await userEvent.click(screen.getByText("Kayıt 05"));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ id: "r5" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Müşteri seç" })).not.toBeInTheDocument());
    expect(input).toHaveValue("Kayıt 05");

    input.focus();
    await userEvent.keyboard("{Enter}");
    expect(await screen.findByRole("dialog", { name: "Müşteri seç" })).toBeInTheDocument();
    // The current pick is the selected radio.
    expect(screen.getByRole("radio", { name: "Kayıt 05" })).toBeChecked();
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Müşteri seç" })).not.toBeInTheDocument());
    input.focus();
    await userEvent.keyboard(" ");
    expect(await screen.findByRole("dialog", { name: "Müşteri seç" })).toBeInTheDocument();
  });

  it("clears the value with the clear button without opening the window", async () => {
    const { source } = makeSource();
    const onChange = vi.fn();
    renderWithProviders(<FieldHarness source={source} onChange={onChange} initial={{ id: "r1", label: "Kayıt 01" }} />);

    await userEvent.click(screen.getByRole("button", { name: "Müşteri alanını temizle" }));

    expect(onChange).toHaveBeenCalledWith(null);
    expect(screen.getByRole("textbox", { name: "Müşteri" })).toHaveValue("");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("is read-only without permission: shows the pre-filled value and never opens", async () => {
    const { source } = makeSource();
    renderWithProviders(<FieldHarness source={source} readOnly initial={{ id: "r1", label: "Kayıt 01" }} />);
    const input = screen.getByRole("textbox", { name: "Müşteri" });

    expect(input).toHaveAttribute("readonly");
    expect(input).toHaveValue("Kayıt 01");
    await userEvent.click(input);
    await userEvent.keyboard("{Enter}");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Müşteri seç" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Müşteri alanını temizle" })).not.toBeInTheDocument();
  });
});
