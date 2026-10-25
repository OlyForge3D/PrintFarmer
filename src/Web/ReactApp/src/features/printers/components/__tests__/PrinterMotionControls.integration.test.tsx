let allowed = true;
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => allowed }) }));
import type { ReactElement } from "react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { USER_SETTINGS_KEY } from "@/features/settings/hooks/useUserSettings";
import {
  act,
  fireEvent,
  render as renderTree,
  screen,
  within,
  waitFor,
} from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  PrinterBackend,
  MmuGateStatus,
  type Printer,
  type PrinterBackendCapabilitiesDto,
  type MmuStatus,
  type ToolheadDto,
} from "@/types/api";
import { DetailedPrinterCard } from "@/features/printers/components/DetailedPrinterCard";
import { PrinterDetailsSidebar } from "@/features/printers/components/PrinterDetailsSidebar";

const mockExecute = vi.fn();
const mockSpeedFactor = vi.fn();
const mockFlowFactor = vi.fn();
const mockToastError = vi.fn();
let mockBlocked = false;
let mockSpoolmanReady = false;
let mockToolheads: ToolheadDto[] | undefined;
vi.mock("@/features/printers/hooks/use-printer-movement", () => ({
  usePrinterMovement: () => ({
    execute: mockExecute,
    blocked: mockBlocked,
  }),
}));
vi.mock("@/common/hooks/usePrinterDisplay", () => ({
  usePrinterDisplay: (printer: Printer) => printer,
}));
vi.mock("@/features/printers/hooks/use-runtime-factors", () => ({
  useRuntimeFactors: () => ({
    speedFactorPercent: 125, flowFactorPercent: 87.5, connected: true,
    epoch: 0, error: '', refresh: vi.fn(), status: { isOnline: true, state: 'Printing' },
  }),
}));
vi.mock("@/common/hooks/useApi", () => ({
  usePrinter: () => ({ data: undefined, isLoading: false, refetch: vi.fn() }),
  usePrinterDetails: () => ({ data: mockToolheads ? { toolheads: mockToolheads } : undefined, isLoading: false }),
  usePrintJobObjects: () => ({
    data: undefined,
    isLoading: false,
    isFetching: false,
    refetch: vi.fn(),
  }),
  queryKeys: {
    printJobObjects: (printerId: string) => ["printJobObjects", printerId],
  },
}));

vi.mock("@/common/hooks/useSpoolmanConfigured", () => ({
  useSpoolmanConfigured: () => ({ ready: mockSpoolmanReady }),
}));

vi.mock("@/services/maintenanceService", () => ({
  maintenanceService: { getPrinterStatistics: vi.fn() },
}));

vi.mock("@/features/printers/hooks/useAutoDispatch", () => ({
  useAutoDispatchStatus: () => ({ data: null, isLoading: false }),
  useSetAutoDispatchEnabled: () => ({ mutateAsync: vi.fn() }),
}));

vi.mock("@/features/printers/hooks/useFailureDetectionAlert", () => ({
  useFailureDetectionAlert: () => ({ event: undefined, recentEvents: [] }),
}));

vi.mock("@/features/printers/hooks/usePrinterFailureDetectionStatus", () => ({
  usePrinterFailureDetectionStatus: () => ({
    printerStatus: undefined,
    data: undefined,
    isLoading: false,
  }),
}));

vi.mock("@/features/printers/hooks/useFailureDetectionPolling", () => ({
  useFailureDetectionPollingEnabled: () => false,
}));

vi.mock(
  "@/features/filament-coverage/components/FilamentCoverageBreakdown",
  () => ({
    FilamentCoverageBreakdown: () => null,
  }),
);

vi.mock("@/features/printers/components/PrinterHistoryModal", () => ({
  PrinterHistoryModal: () => null,
}));
vi.mock("@/features/printers/components/PrinterFilesModal", () => ({
  PrinterFilesModal: () => null,
}));
vi.mock("@/features/printers/components/SpoolPickerModal", () => ({
  SpoolPickerModal: () => null,
}));
vi.mock("@/features/printers/components/MaterialLoadout", () => ({
  MaterialLoadout: () => <div data-testid="material-loadout" />,
}));
vi.mock("@/features/printers/components/MmuControlBox", () => ({
  MmuControlBox: () => <div data-testid="mmu-controls" />,
}));
vi.mock("@/features/printers/components/TemperatureControlSection", () => ({
  TemperatureControlSection: () => <div data-testid="temp-section" />,
}));
vi.mock("@/features/printers/components/BedClearBanner", () => ({
  BedClearBanner: () => null,
}));
vi.mock("@/features/printers/components/PrintProgressBar", () => ({
  PrintProgressBar: () => <div data-testid="print-progress" />,
}));
vi.mock("@/features/printers/components/EstimatedCompletionBadge", () => ({
  EstimatedCompletionBadge: () => null,
}));
vi.mock("@/features/printers/components/FailureDetectionBadge", () => ({
  FailureDetectionBadge: () => null,
}));
vi.mock(
  "@/features/printers/components/FailureDetectionMonitoringBadge",
  () => ({ FailureDetectionMonitoringBadge: () => null }),
);
vi.mock(
  "@/features/printers/components/FailureDetectionMonitoringSummary",
  () => ({ FailureDetectionMonitoringSummary: () => null }),
);
vi.mock("@/features/printers/components/PrinterCameraPreview", () => ({
  PrinterCameraPreview: () => <div data-testid="camera-preview" />,
}));
vi.mock("@/services/api", () => ({
  apiClient: {
    setPrinterSpeedFactor: (...args: unknown[]) => mockSpeedFactor(...args),
    setPrinterFlowFactor: (...args: unknown[]) => mockFlowFactor(...args),
    put: async (_url: string, body: unknown) => ({
      data: { userId: "motion-user", rowVersion: "v2", ...(body as object) },
    }),
  },
}));
vi.mock("sonner", () => ({
  toast: {
    success: vi.fn(),
    error: (...args: unknown[]) => mockToastError(...args),
    info: vi.fn(),
  },
}));

const printer: Printer = {
  id: "motion-fixture",
  name: "Motion fixture",
  backend: PrinterBackend.Moonraker,
  isOnline: true,
  backendUrl: "http://printer.local",
  isReachable: true,
  isEnabled: true,
  state: "Idle",
  homedAxes: "xyz",
  x: 100,
  y: 110,
  z: 5,
  hotendTemp: 25,
  bedTemp: 23,
};
const capabilities = {
  printerId: printer.id,
  supportsMovement: true,
  supportsControlOperations: true,
  supportsTemperatureControl: true,
  supportsFilamentControl: false,
  supportsObjectExclusion: false,
} as PrinterBackendCapabilitiesDto;
type Surface = "detail" | "sidebar";
function Controls({ surface, overrides = {}, movement = true, factors = false }: { surface: Surface; overrides?: Partial<Printer & { mmuStatus: MmuStatus }>; movement?: boolean; factors?: boolean }) {
  const current = { ...printer, ...overrides };
  const support = { ...capabilities, supportsMovement: movement, supportsSpeedFactorControl: factors, supportsFlowFactorControl: factors };
  return surface === "detail" ? (
    <DetailedPrinterCard printer={current} backendCapabilities={support} />
  ) : (
    <PrinterDetailsSidebar
      printerId={printer.id}
      printer={current}
      backendCapabilities={support}
      onClose={vi.fn()}
    />
  );
}
function fillCoordinates(scope: ReturnType<typeof within> = screen) {
  for (const [axis, value] of [
    ["X", "120"],
    ["Y", "130"],
    ["Z", "15"],
  ]) {
    fireEvent.change(scope.getByLabelText(`${axis} absolute target`), {
      target: { value },
    });
  }
}

let accountMode: "Guided" | "Expert" = "Guided";
function render(element: ReactElement) {
  const client = new QueryClient({
    defaultOptions: { queries: { enabled: false, retry: false } },
  });
  client.setQueryData(USER_SETTINGS_KEY, {
    userId: "motion-user",
    printerControlMode: accountMode,
    rowVersion: "v1",
  });
  return {
    client,
    ...renderTree(
      <QueryClientProvider client={client}>{element}</QueryClientProvider>,
    ),
  };
}

beforeEach(() => {
  allowed = true;
  accountMode = "Guided";
  mockBlocked = false;
  mockSpoolmanReady = false;
  mockToolheads = undefined;
  mockExecute.mockReset();
  mockSpeedFactor.mockReset().mockResolvedValue({ success: true });
  mockFlowFactor.mockReset().mockResolvedValue({ success: true });
  mockToastError.mockReset();
});

describe.each<Surface>(["detail", "sidebar"])(
  "%s initiating motion controls",
  (surface) => {
    it('renders Tuning sliders and sends the independent wire percentage once on release on this surface', async () => {
      render(<Controls surface={surface} factors overrides={{ state: 'Printing' }} />);
      expect(screen.getByLabelText('Observed speed')).toHaveTextContent('125%');
      expect(screen.getByLabelText('Observed flow')).toHaveTextContent('87.5%');
      const tuning = within(screen.getByText('Tuning').closest('section')!);
      expect(tuning.queryByRole('spinbutton')).not.toBeInTheDocument();
      expect(tuning.queryByRole('button', { name: /Apply/ })).not.toBeInTheDocument();
      const slider = tuning.getByRole('slider', { name: 'Speed %' });
      fireEvent.pointerDown(slider);
      fireEvent.change(slider, { target: { value: '130.25' } });
      expect(mockSpeedFactor).not.toHaveBeenCalled();
      fireEvent.pointerUp(slider);
      fireEvent.blur(slider);
      await waitFor(() => expect(mockSpeedFactor).toHaveBeenCalledExactlyOnceWith(printer.id, 130.25));
      expect(mockFlowFactor).not.toHaveBeenCalled();
      expect(screen.getByLabelText('Observed speed')).toHaveTextContent('125%');
    });

    it('collapses and reopens Tuning without resetting or replaying a pending command', async () => {
      const user = userEvent.setup();
      let finish!: (result: { success: boolean }) => void;
      mockSpeedFactor.mockImplementation(() => new Promise(resolve => { finish = resolve; }));
      render(<Controls surface={surface} factors overrides={{ state: 'Printing' }} />);
      const speed = screen.getByRole('slider', { name: 'Speed %' });
      fireEvent.pointerDown(speed);
      fireEvent.change(speed, { target: { value: '131.5' } });
      fireEvent.pointerUp(speed);
      await waitFor(() => expect(mockSpeedFactor).toHaveBeenCalledExactlyOnceWith(printer.id, 131.5));
      const toggle = screen.getByRole('button', { name: /Collapse Tuning/ });
      await user.click(toggle);
      expect(toggle).toHaveAttribute('aria-expanded', 'false');
      expect(speed).toBeInTheDocument();
      expect(speed).not.toBeVisible();
      expect(screen.queryByRole('slider', { name: 'Speed %' })).not.toBeInTheDocument();
      await user.keyboard('{Enter}');
      expect(toggle).toHaveAttribute('aria-expanded', 'true');
      expect(screen.getByRole('slider', { name: 'Speed %' })).toBe(speed);
      expect(speed).toBeDisabled();
      expect(screen.getByText('Sending...')).toBeVisible();
      await user.keyboard(' ');
      expect(speed).not.toBeVisible();
      await act(async () => finish({ success: true }));
      await user.keyboard('{Enter}');
      expect(speed).toBeVisible();
      expect(speed).toBeEnabled();
      expect(screen.getByText('Acknowledged 131.5%. Awaiting live readback.')).toBeVisible();
      expect(screen.getByLabelText('Observed speed')).toHaveTextContent('125%');
      expect(mockSpeedFactor).toHaveBeenCalledTimes(1);
      expect(mockFlowFactor).not.toHaveBeenCalled();
      expect(mockExecute).not.toHaveBeenCalled();
    });

    it.each(['spool', 'configured-empty', 'scalar-spool', 'toolheads', 'mmu', 'empty-mmu'] as const)(
      'uses a functional Materials disclosure for %s without remounting its content',
      async variant => {
        const user = userEvent.setup();
        mockSpoolmanReady = variant === 'configured-empty' || variant === 'empty-mmu';
        if (variant === 'toolheads') mockToolheads = [
          { id: 't0', index: 0, isPrimary: true, toolheadType: 'Physical' },
          { id: 't1', index: 1, isPrimary: false, toolheadType: 'Physical' },
        ];
        const mmu: MmuStatus = {
          enabled: true, isHomed: true, activeTool: 0, activeGate: 0, numGates: 4,
          hasBypass: false, endlessSpool: false, clogDetection: false, mmuType: 'HappyHare',
          gates: variant === 'empty-mmu' ? [] : [{ index: 0, status: MmuGateStatus.Available, spoolId: -1, material: 'PLA' }],
        };
        const overrides = variant === 'mmu' || variant === 'empty-mmu' ? { mmuStatus: mmu }
          : variant === 'spool' ? { spoolInfo: { hasActiveSpool: true, activeSpoolId: 7, material: 'PLA' } }
          : variant === 'scalar-spool' ? { currentSpoolId: 7 } : {};
        render(<Controls surface={surface} factors overrides={{ ...overrides, state: 'Printing' }} />);
        const toggle = screen.getByRole('button', { name: /Collapse Materials/ });
        const panel = document.getElementById(toggle.getAttribute('aria-controls')!)!;
        const body = panel.firstElementChild;
        expect(panel).toBeVisible();
        expect(screen.queryByText('Spool', { exact: true })).not.toBeInTheDocument();
        if (variant.includes('mmu')) {
          expect(screen.getByTestId('mmu-controls')).toBeVisible();
          expect(screen.queryByTestId('material-loadout')).not.toBeInTheDocument();
        } else if (variant === 'toolheads') expect(screen.getByTestId('material-loadout')).toBeVisible();
        await user.click(toggle);
        expect(toggle).toHaveAttribute('aria-expanded', 'false');
        expect(panel).not.toBeVisible();
        expect(panel).toBeInTheDocument();
        expect(screen.getByRole('slider', { name: 'Speed %' })).toBeVisible();
        await user.keyboard('{Enter}');
        expect(panel).toBeVisible();
        await user.keyboard(' ');
        expect(panel).not.toBeVisible();
        await user.keyboard('{Enter}');
        expect(panel.firstElementChild).toBe(body);
        expect(panel).toBeVisible();
        expect(mockSpeedFactor).not.toHaveBeenCalled();
        expect(mockFlowFactor).not.toHaveBeenCalled();
        expect(mockExecute).not.toHaveBeenCalled();
      },
    );

    it('omits Materials when there is no material content or Spoolman integration', () => {
      render(<Controls surface={surface} />);
      expect(screen.queryByText('Materials', { exact: true })).not.toBeInTheDocument();
    });

    it('keeps runtime controls hidden without operation capability on this surface', () => {
      render(<Controls surface={surface} />);
      expect(screen.queryByRole('slider', { name: 'Speed %' })).not.toBeInTheDocument();
      expect(screen.queryByRole('slider', { name: 'Flow %' })).not.toBeInTheDocument();
      expect(mockSpeedFactor).not.toHaveBeenCalled();
      expect(mockFlowFactor).not.toHaveBeenCalled();
    });

    it('preserves Queue.Start authorization for runtime controls on this surface', () => {
      allowed = false;
      render(<Controls surface={surface} factors overrides={{ state: 'Printing' }} />);
      expect(screen.getByRole('slider', { name: 'Speed %' })).toBeDisabled();
      expect(screen.getByRole('slider', { name: 'Flow %' })).toBeDisabled();
      expect(mockSpeedFactor).not.toHaveBeenCalled();
      expect(mockFlowFactor).not.toHaveBeenCalled();
    });

    it.each(['Idle', 'Paused', 'Starting', 'Completed', 'Unknown'])('disables Tuning while %s on this surface', state => {
      render(<Controls surface={surface} factors overrides={{ state }} />);
      expect(screen.getByRole('slider', { name: 'Speed %' })).toBeDisabled();
      expect(screen.getByRole('slider', { name: 'Flow %' })).toBeDisabled();
      expect(mockSpeedFactor).not.toHaveBeenCalled();
      expect(mockFlowFactor).not.toHaveBeenCalled();
    });

    it.each([
      { isOnline: false }, { isEnabled: false }, { homedAxes: undefined }, { homedAxes: '' },
      { homedAxes: 'xy' }, { homedAxes: 'x' }, { homedAxes: 'unknown' },
      { state: 'Printing' }, { state: 'Shutdown' },
    ])('locks coordinate fields for %j', overrides => {
      render(<Controls surface={surface} overrides={overrides} />);
      for (const input of screen.getAllByRole('spinbutton', { name: /absolute target/ })) expect(input).toBeDisabled();
    });

    it.each(['Absolute', 'Relative'])('discards a dirty %s edit if any required homing is lost on the actual surface', mode => {
      const { client, rerender } = render(<Controls surface={surface} />);
      fireEvent.click(screen.getByRole('button', { name: mode }));
      const name = mode === 'Absolute' ? 'X absolute target' : 'X movement amount';
      const field = screen.getByLabelText(name);
      fireEvent.focus(field);
      fireEvent.change(field, { target: { value: '25' } });
      rerender(<QueryClientProvider client={client}><Controls surface={surface} overrides={{ homedAxes: 'xy' }} /></QueryClientProvider>);
      expect(screen.getByLabelText(name)).toBeDisabled();
      expect(screen.getByLabelText(name)).toHaveValue(null);
      rerender(<QueryClientProvider client={client}><Controls surface={surface} /></QueryClientProvider>);
      fireEvent.keyDown(screen.getByLabelText(name), { key: 'Enter' });
      fireEvent.blur(screen.getByLabelText(name));
      expect(mockExecute).not.toHaveBeenCalled();
    });

    it.each(['permission', 'capability', 'pending'])('locks coordinate fields without %s', gate => {
      allowed = gate !== 'permission';
      mockBlocked = gate === 'pending';
      render(<Controls surface={surface} movement={gate !== 'capability'} />);
      for (const input of screen.getAllByRole('spinbutton', { name: /absolute target/ })) expect(input).toBeDisabled();
      expect(mockExecute).not.toHaveBeenCalled();
    });

    it('submits relative X alone on blur exactly once when XYZ are homed', async () => {
      mockExecute.mockResolvedValue({ success: true });
      render(<Controls surface={surface} />);
      expect(screen.getByLabelText('Z absolute target')).toBeEnabled();
      fireEvent.click(screen.getByRole('button', { name: 'Relative' }));
      const x = screen.getByLabelText('X movement amount');
      fireEvent.focus(x);
      fireEvent.change(x, { target: { value: '-2.5' } });
      fireEvent.blur(x);
      fireEvent.blur(x);
      await waitFor(() => expect(mockExecute).toHaveBeenCalledExactlyOnceWith({ kind: 'Jog', x: -2.5 }));
    });

    it.each([
      ["Home all axes", { kind: "HomeAll" }],
      ["Home X/Y", { kind: "HomeXY" }],
      ["Home Z", { kind: "HomeZ" }],
      ["Jog Y positive", { kind: "Jog", y: surface === "detail" ? 1 : 10 }],
      ["Jog Y negative", { kind: "Jog", y: surface === "detail" ? -1 : -10 }],
      ["Jog X positive", { kind: "Jog", x: surface === "detail" ? 1 : 10 }],
      ["Jog Z negative", { kind: "Jog", z: surface === "detail" ? -1 : -10 }],
      ["Jog X negative", { kind: "Jog", x: surface === "detail" ? -1 : -10 }],
      ["Jog Z positive", { kind: "Jog", z: surface === "detail" ? 1 : 10 }],
    ])(
      "shows activity only on %s immediately, preserving the exact motion intent",
      async (name, intent) => {
        let finish!: () => void;
        mockExecute.mockImplementation(
          () =>
            new Promise((resolve) => {
              finish = () => resolve({ success: true });
            }),
        );
        const { container } = render(<Controls surface={surface} />);
        fillCoordinates();
        const initiating = screen.getByRole("button", { name });
        fireEvent.click(initiating);
        expect(initiating).toHaveAttribute("aria-busy", "true");
        expect(initiating).toBeDisabled();
        expect(
          container.querySelectorAll('button[aria-busy="true"]'),
        ).toHaveLength(1);
        expect(
          screen.getByRole("button", { name: "Home all axes" }),
        ).toBeDisabled();
        expect(
          screen.getByRole("button", { name: "Jog Y positive" }),
        ).toBeDisabled();
        expect(
          screen.getByRole("button", { name: "Absolute" }),
        ).toBeDisabled();
        expect(mockExecute).toHaveBeenCalledExactlyOnceWith(intent);
        fireEvent.click(initiating);
        expect(mockExecute).toHaveBeenCalledTimes(1);
        await act(async () => finish());
        expect(initiating).toHaveAttribute("aria-busy", "false");
        expect(initiating).toBeEnabled();
      },
    );

    it("does not mark another initiating button busy while a shared request is pending", () => {
      mockBlocked = true;
      const { container } = render(<Controls surface={surface} />);
      expect(
        screen.getByRole("button", { name: "Home all axes" }),
      ).toBeDisabled();
      expect(
        container.querySelectorAll('button[aria-busy="true"]'),
      ).toHaveLength(0);
      expect(mockExecute).not.toHaveBeenCalled();
    });

    it("sends only the edited axis on absolute Enter without tracking UI", async () => {
      mockExecute.mockResolvedValue({ success: true });
      render(<Controls surface={surface} />);
      fireEvent.change(screen.getByLabelText("X absolute target"), {
        target: { value: "12" },
      });
      expect(screen.queryByText("GO")).not.toBeInTheDocument();
      expect(
        screen.queryByText(
          /Motion Ready|Operation ID|Recheck status|Recovery required/i,
        ),
      ).not.toBeInTheDocument();
      await act(async () =>
        fireEvent.keyDown(screen.getByLabelText("X absolute target"), { key: "Enter" }),
      );
      expect(mockExecute).toHaveBeenCalledExactlyOnceWith({
        kind: "MoveTo",
        x: 12,
      });
    });

    it("retains the shared motion lock and stop access in Expert", () => {
      accountMode = "Expert";
      mockBlocked = true;
      render(<Controls surface={surface} />);
      const stop = screen.getByTitle("Emergency Stop");
      expect(stop).toBeEnabled();
      expect(
        screen.getByRole("button", { name: "Home all axes" }),
      ).toBeDisabled();
      expect(
        screen.getByRole("button", { name: "Jog Y positive" }),
      ).toBeDisabled();
      expect(stop).toBeEnabled();
      expect(mockExecute).not.toHaveBeenCalled();
    });

    it("clears activity and retains actionable failure feedback in Expert", async () => {
      accountMode = "Expert";
      let finish!: () => void;
      mockExecute.mockImplementation(
        () =>
          new Promise((resolve) => {
            finish = () =>
              resolve({ success: false, error: "Printer rejected motion" });
          }),
      );
      const errorLog = vi.spyOn(console, "error").mockImplementation(() => {});
      render(<Controls surface={surface} />);
      const button = screen.getByRole("button", { name: "Home all axes" });
      fireEvent.click(button);
      await act(async () => finish());
      expect(button).toHaveAttribute("aria-busy", "false");
      expect(mockToastError).toHaveBeenCalledWith("Printer rejected motion");
      errorLog.mockRestore();
    });
  },
);

it("follows the User Settings mode on actual detail/sidebar surfaces without a local selector", async () => {
  const { client } = render(
    <>
      <section aria-label="detail">
        <Controls surface="detail" />
      </section>
      <section aria-label="sidebar">
        <Controls surface="sidebar" />
      </section>
    </>,
  );
  const detail = within(screen.getByRole("region", { name: "detail" }));
  const sidebar = within(screen.getByRole("region", { name: "sidebar" }));
  for (const surface of [detail, sidebar]) {
    expect(
      surface.queryByRole("group", { name: "Printer controls mode" }),
    ).not.toBeInTheDocument();
    expect(surface.queryByRole("button", { name: "Guided" })).not.toBeInTheDocument();
    expect(surface.queryByRole("button", { name: "Expert" })).not.toBeInTheDocument();
    expect(surface.getByText(/Jog moves by/)).toBeVisible();
  }
  act(() => {
    client.setQueryData(USER_SETTINGS_KEY, {
      userId: "motion-user",
      printerControlMode: "Expert",
      rowVersion: "v2",
    });
  });
  await waitFor(() =>
    expect(screen.queryByText(/Jog moves by/)).not.toBeInTheDocument(),
  );
  expect(screen.queryByText(/Motion help/i)).not.toBeInTheDocument();
  expect(screen.getAllByRole("spinbutton")).toHaveLength(6);
  expect(mockExecute).not.toHaveBeenCalled();
});

it("sends one direct absolute command from sidebar Enter", async () => {
  mockExecute.mockResolvedValue({ success: true });
  render(<Controls surface="sidebar" />);
  fillCoordinates();
  await act(async () =>
    fireEvent.keyDown(screen.getByLabelText("Y absolute target"), {
      key: "Enter",
    }),
  );
  expect(mockExecute).toHaveBeenCalledExactlyOnceWith({
    kind: "MoveTo",
    y: 130,
  });
});
