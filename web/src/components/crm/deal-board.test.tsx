import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { toastApiError } from "@/hooks/use-toast";
import { useDealBoard } from "@/hooks/use-deals";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, setPermissions, type MockClient } from "@/test/crm";
import type { DealBoard } from "@/types";
import { DealBoardView } from "./deal-board";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const BOARD: DealBoard = {
  pipelineId: "p1",
  stages: [
    {
      id: "s1",
      name: "Nitelendirme",
      kind: "open",
      probability: 10,
      count: 2,
      totalAmount: 1500,
      deals: [
        {
          id: "d1",
          name: "Alfa",
          accountName: "Acme",
          amount: 1000,
          currency: "TRY",
          ownerName: "Ada",
        },
        { id: "d2", name: "Beta", accountName: "Globex", amount: 500, currency: "TRY" },
      ],
    },
    {
      id: "s2",
      name: "Teklif",
      kind: "open",
      probability: 50,
      count: 0,
      totalAmount: 0,
      deals: [],
    },
    {
      id: "s3",
      name: "Kazanıldı",
      kind: "won",
      probability: 100,
      count: 0,
      totalAmount: 0,
      deals: [],
    },
    {
      id: "s4",
      name: "Kaybedildi",
      kind: "lost",
      probability: 0,
      count: 0,
      totalAmount: 0,
      deals: [],
    },
  ],
};

const QUERY = { pipelineId: "p1" };

function Harness({ canMove = true }: { canMove?: boolean }) {
  const board = useDealBoard(QUERY);
  return board.data ? (
    <DealBoardView board={board.data} boardQuery={QUERY} canMove={canMove} />
  ) : (
    <div>loading</div>
  );
}

const column = (id: string) => screen.getByTestId(`stage-${id}`);
const moveButton = (name: string) => screen.getByRole("button", { name: `${name} fırsatını taşı` });

async function moveTo(dealName: string, stageName: string) {
  await userEvent.click(moveButton(dealName));
  await userEvent.click(await screen.findByRole("menuitem", { name: stageName }));
}

/** GET /deals/board answers once; later refetches (after settle) hang so only the optimistic/rollback state is visible. */
function installBoard(post: () => unknown) {
  let gets = 0;
  installApi(client, {
    "GET /deals/board": () => (++gets === 1 ? BOARD : new Promise(() => undefined)),
    "POST /deals/d1/stage": post,
  });
}

describe("DealBoardView stage moves", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["crm.deals.read", "crm.deals.write"]);
  });
  afterEach(clearSession);

  it("renders a column per stage with its count and total", async () => {
    installBoard(() => undefined);
    renderWithProviders(<Harness />);

    await screen.findByText("Alfa");
    expect(within(column("s1")).getByText("Nitelendirme")).toBeInTheDocument();
    expect(within(column("s1")).getByLabelText("2 fırsat")).toBeInTheDocument();
    expect(screen.getByTestId("stage-total-s1")).toHaveTextContent(/1\.500/);
    expect(within(column("s2")).getByText("Bu aşamada fırsat yok")).toBeInTheDocument();
  });

  it("moves the card optimistically, before the server answers, and sends the stage", async () => {
    let finish!: () => void;
    installBoard(() => new Promise<void>((resolve) => (finish = resolve)));
    renderWithProviders(<Harness />);
    await screen.findByText("Alfa");

    await moveTo("Alfa", "Teklif");

    // The POST has not resolved yet, but the card is already in the new column with updated numbers.
    await waitFor(() => expect(within(column("s2")).getByText("Alfa")).toBeInTheDocument());
    expect(within(column("s1")).queryByText("Alfa")).not.toBeInTheDocument();
    expect(within(column("s2")).getByLabelText("1 fırsat")).toBeInTheDocument();
    expect(screen.getByTestId("stage-total-s1")).toHaveTextContent(/500/);
    expect(client.post).toHaveBeenCalledWith("/deals/d1/stage", {
      stageId: "s2",
      lostReason: undefined,
    });
    finish();
  });

  it("rolls the card back and reports the error when the server rejects the move", async () => {
    let reject!: (error: Error) => void;
    installBoard(() => new Promise((_, r) => (reject = r)));
    renderWithProviders(<Harness />);
    await screen.findByText("Alfa");

    await moveTo("Alfa", "Kazanıldı");
    await waitFor(() => expect(within(column("s3")).getByText("Alfa")).toBeInTheDocument());

    const error = problem(409, {
      status: 409,
      title: "Conflict",
      code: "pipeline.stage_not_found",
    });
    reject(error);

    await waitFor(() => expect(within(column("s1")).getByText("Alfa")).toBeInTheDocument());
    expect(within(column("s3")).queryByText("Alfa")).not.toBeInTheDocument();
    expect(within(column("s1")).getByLabelText("2 fırsat")).toBeInTheDocument();
    expect(screen.getByTestId("stage-total-s1")).toHaveTextContent(/1\.500/);
    expect(toastApiError).toHaveBeenCalledWith(error);
  });

  it("asks for a lost reason when moving to a lost stage and only then calls the API", async () => {
    installBoard(() => undefined);
    renderWithProviders(<Harness />);
    await screen.findByText("Alfa");

    await moveTo("Alfa", "Kaybedildi");

    const dialog = await screen.findByRole("dialog");
    expect(client.post).not.toHaveBeenCalled();
    // Nothing moved yet: the card is still in its column.
    expect(within(column("s1")).getByText("Alfa")).toBeInTheDocument();

    await userEvent.click(
      within(dialog).getByRole("button", { name: "Kaybedildi olarak işaretle" })
    );
    expect(await within(dialog).findByText("Bu alan zorunludur")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();

    await userEvent.type(within(dialog).getByLabelText(/Kayıp nedeni/), "Bütçe yok");
    await userEvent.click(
      within(dialog).getByRole("button", { name: "Kaybedildi olarak işaretle" })
    );

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post).toHaveBeenCalledWith("/deals/d1/stage", {
      stageId: "s4",
      lostReason: "Bütçe yok",
    });
    await waitFor(() => expect(within(column("s4")).getByText("Alfa")).toBeInTheDocument());
  });

  it("can cancel the lost dialog without moving anything", async () => {
    installBoard(() => undefined);
    renderWithProviders(<Harness />);
    await screen.findByText("Alfa");

    await moveTo("Alfa", "Kaybedildi");
    await userEvent.click(
      within(await screen.findByRole("dialog")).getByRole("button", { name: "Vazgeç" })
    );

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
    expect(within(column("s1")).getByText("Alfa")).toBeInTheDocument();
  });

  it("rolls back a lost move when the server rejects it", async () => {
    const error = problem(400, { status: 400, title: "Bad", code: "deal.lost_reason_required" });
    installBoard(() => error);
    renderWithProviders(<Harness />);
    await screen.findByText("Alfa");

    await moveTo("Alfa", "Kaybedildi");
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/Kayıp nedeni/), "x");
    await userEvent.click(
      within(dialog).getByRole("button", { name: "Kaybedildi olarak işaretle" })
    );

    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
    await waitFor(() => expect(within(column("s1")).getByText("Alfa")).toBeInTheDocument());
    expect(within(column("s4")).queryByText("Alfa")).not.toBeInTheDocument();
  });

  it("offers no move controls without deal write permission", async () => {
    installBoard(() => undefined);
    setPermissions(["crm.deals.read"]);
    renderWithProviders(<Harness canMove={false} />);

    await screen.findByText("Alfa");
    expect(screen.queryByRole("button", { name: /fırsatını taşı/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /fırsatını sürükle/ })).not.toBeInTheDocument();
  });
});
