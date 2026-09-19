/**
 * Stand-ins for `@mantine/charts` in component tests: recharts measures its container, which is
 * always 0x0 in jsdom, so the real charts render nothing. The stubs expose the data they receive.
 *
 * Usage: `vi.mock("@mantine/charts", async () => (await import("@/test/charts")).chartMocks);`
 */
function stub(name: string) {
  return function ChartStub({ data }: { data?: unknown[] }) {
    return <div data-testid={`chart-${name}`} data-points={JSON.stringify(data ?? [])} />;
  };
}

export const chartMocks = {
  BarChart: stub("bar"),
  DonutChart: stub("donut"),
  FunnelChart: stub("funnel"),
};
