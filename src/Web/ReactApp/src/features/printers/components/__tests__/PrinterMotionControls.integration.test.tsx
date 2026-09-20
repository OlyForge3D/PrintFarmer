import type { ReactElement } from "react";
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
  type Printer,
  type PrinterBackendCapabilitiesDto,
} from "@/types/api";
import { DetailedPrinterCard } from "@/features/printers/components/DetailedPrinterCard";
import { PrinterDetailsSidebar } from "@/features/printers/components/PrinterDetailsSidebar";

const mockExecute = vi.fn();
const mockToastError = vi.fn();
let mockBlocked = false;
vi.mock("@/features/printers/hooks/use-printer-movement", () => ({
  usePrinterMovement: () => ({
    execute: mockExecute,
    blocked: mockBlocked,
  }),
}));
vi.mock("@/common/hooks/usePrinterDisplay", () => ({
  usePrinterDisplay: (printer: Printer) => printer,
}));
vi.mock("@/common/hooks/useApi", () => ({
  usePrinter: () => ({ data: undefined, isLoading: false, refetch: vi.fn() }),
  usePrinterDetails: () => ({ data: undefined, isLoading: false }),
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
  useSpoolmanConfigured: () => ({ ready: false }),
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
vi.mock("@/features/printers/components/TemperatureControlSection", () => ({
  TemperatureControlSection: () => <div data-testid="temp-section" />,
}));
vi.mock("@/features/printers/components/FilamentControlSection", () => ({
  FilamentControlSection: () => <div data-testid="filament-section" />,
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
vi.mock("@/features/printers/components/ZOffsetCalibrationWizard", () => ({
  ZOffsetCalibrationWizard: () => <div data-testid="zoffset-wizard" />,
}));
vi.mock("@/services/api", () => ({
  apiClient: {
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
  supportsMovement: true,
  supportsControlOperations: true,
  supportsTemperatureControl: true,
  supportsFilamentControl: false,
  supportsObjectExclusion: false,
} as PrinterBackendCapabilitiesDto;
type Surface = "detail" | "sidebar";
function Controls({ surface }: { surface: Surface }) {
  return surface === "detail" ? (
    <DetailedPrinterCard printer={printer} backendCapabilities={capabilities} />
  ) : (
    <PrinterDetailsSidebar
      printerId={printer.id}
      printer={printer}
      backendCapabilities={capabilities}
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
  return renderTree(
    <QueryClientProvider client={client}>{element}</QueryClientProvider>,
  );
}

beforeEach(() => {
  accountMode = "Guided";
  mockBlocked = false;
  mockExecute.mockReset();
  mockToastError.mockReset();
});

describe.each<Surface>(["detail", "sidebar"])(
  "%s initiating motion controls",
  (surface) => {
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
      ["GO to absolute position", { kind: "MoveTo", x: 120, y: 130, z: 15 }],
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
          screen.getByRole("button", { name: "GO to absolute position" }),
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

    it("requires all coordinates for direct absolute GO without tracking UI", async () => {
      mockExecute.mockResolvedValue({ success: true });
      render(<Controls surface={surface} />);
      fireEvent.change(screen.getByLabelText("X absolute target"), {
        target: { value: "12" },
      });
      expect(
        screen.getByRole("button", { name: "GO to absolute position" }),
      ).toBeDisabled();
      expect(
        screen.queryByText(
          /Motion Ready|Operation ID|Recheck status|Recovery required/i,
        ),
      ).not.toBeInTheDocument();
      fillCoordinates();
      await act(async () =>
        fireEvent.click(
          screen.getByRole("button", { name: "GO to absolute position" }),
        ),
      );
      expect(mockExecute).toHaveBeenCalledExactlyOnceWith({
        kind: "MoveTo",
        x: 120,
        y: 130,
        z: 15,
      });
    });

    it("retains the shared motion lock and stop access when switching to Expert", async () => {
      mockBlocked = true;
      render(<Controls surface={surface} />);
      const stop = screen.getByTitle("Emergency Stop");
      expect(stop).toBeEnabled();
      fireEvent.click(screen.getByRole("button", { name: "Expert" }));
      expect(
        screen.getByRole("button", { name: "Home all axes" }),
      ).toBeDisabled();
      expect(
        screen.getByRole("button", { name: "Jog Y positive" }),
      ).toBeDisabled();
      expect(stop).toBeEnabled();
      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Expert" })).toBeEnabled(),
      );
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

it("synchronizes actual detail/sidebar modes and keeps both coordinate rows", async () => {
  render(
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
  expect(detail.getByRole("button", { name: "Guided" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  expect(sidebar.getByRole("button", { name: "Guided" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  fireEvent.click(detail.getByRole("button", { name: "Expert" }));
  await waitFor(() =>
    expect(sidebar.getByRole("button", { name: "Expert" })).toHaveAttribute(
      "aria-pressed",
      "true",
    ),
  );
  await waitFor(() =>
    expect(sidebar.getByRole("button", { name: "Expert" })).toBeEnabled(),
  );
  expect(detail.getByText("Motion help")).toBeVisible();
  expect(sidebar.getByText("Motion help")).toBeVisible();
  expect(detail.getByText(/Jog moves by/)).not.toBeVisible();
  expect(sidebar.getByText(/Jog moves by/)).not.toBeVisible();
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
    x: 120,
    y: 130,
    z: 15,
  });
});
