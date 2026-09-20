vi.mock("@/features/printers/hooks/use-printer-controls-mode", () => ({
  usePrinterControlsMode: () => ({
    mode: "guided",
    canSave: true,
    setMode: vi.fn(),
    reload: vi.fn(),
  }),
}));

import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import type { UseQueryOptions } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import {
  PrinterBackend,
  type CommandResult,
  type Printer,
  type PrinterBackendCapabilitiesDto,
  type PrintJobObjectListDto,
} from "@/types/api";
import { PrinterDetailsSidebar } from "../PrinterDetailsSidebar";
import type { PrinterStatistics } from "@/types/maintenance";
import { AuthContext } from "@/common/contexts/auth-context";
import type { AuthContextType } from "@/contexts/AuthContextValue";
import type { PrinterMovement } from "@/features/printers/hooks/use-printer-movement";

const mockInvalidateQueries = vi.fn();
const mockSetQueryData = vi.fn();
const mockRefetch = vi.fn();
const mockPrintJobObjectsRefetch = vi.fn();
const mockExcludePrintJobObject = vi.fn();
const mockHomePrinter = vi.fn();
const mockHomeXY = vi.fn();
const mockHomeZ = vi.fn();
const mockMovePrinter = vi.fn();
const mockToastError = vi.fn();
const mockMovePrinterTo = vi.fn();
vi.mock("@/features/printers/hooks/use-printer-movement", () => ({
  usePrinterMovement: (printer: Printer) => ({
    blocked: false,
    execute: async ({ kind, ...move }: PrinterMovement) => {
      switch (kind) {
        case "HomeAll":
          return mockHomePrinter(printer.id);
        case "HomeXY":
          return mockHomeXY(printer.id);
        case "HomeZ":
          return mockHomeZ(printer.id);
        case "Jog":
          return mockMovePrinter(printer.id, move);
        case "MoveTo":
          return mockMovePrinterTo(printer.id, move);
      }
    },
  }),
}));
let capturedStatisticsQueryOptions:
  UseQueryOptions<PrinterStatistics> | undefined;
let mockStatisticsData: PrinterStatistics | undefined;
let mockPrintJobObjectsData: PrintJobObjectListDto | undefined;
let mockVersionData: Record<string, unknown> | undefined;

vi.mock("sonner", () => ({
  toast: {
    error: (...args: unknown[]) => mockToastError(...args),
    success: vi.fn(),
  },
}));

vi.mock("@tanstack/react-query", () => ({
  useQuery: (options: UseQueryOptions<PrinterStatistics>) => {
    if (
      Array.isArray(options.queryKey) &&
      options.queryKey[0] === "printerStatistics"
    ) {
      capturedStatisticsQueryOptions = options;
      return {
        data: mockStatisticsData,
        isLoading: false,
        refetch: mockRefetch,
      };
    }

    if (
      Array.isArray(options.queryKey) &&
      options.queryKey[0] === "printerVersion"
    ) {
      return {
        data: mockVersionData,
        isLoading: false,
        refetch: mockRefetch,
      };
    }

    return {
      data: undefined,
      isLoading: false,
      refetch: mockRefetch,
    };
  },
  useQueryClient: () => ({
    invalidateQueries: mockInvalidateQueries,
    setQueryData: mockSetQueryData,
  }),
  useMutation: (options: {
    mutationFn: (name: string) => Promise<CommandResult>;
    onSuccess?: (result: CommandResult, name: string) => void | Promise<void>;
    onError?: (error: Error) => void;
  }) => ({
    isPending: false,
    mutate: (name: string) => {
      void options
        .mutationFn(name)
        .then((result) => options.onSuccess?.(result, name))
        .catch((error: Error) => options.onError?.(error));
    },
  }),
}));

vi.mock("@/common/hooks/useApi", () => ({
  queryKeys: {
    printJobObjects: (printerId: string) => [
      "printers",
      printerId,
      "printjob",
      "objects",
    ],
  },
  usePrinter: () => ({
    data: undefined,
    isLoading: false,
    refetch: mockRefetch,
  }),
  usePrinterDetails: () => ({ data: undefined }),
  usePrintJobObjects: () => ({
    data: mockPrintJobObjectsData,
    isLoading: false,
    isFetching: false,
    refetch: mockPrintJobObjectsRefetch,
  }),
}));

vi.mock("@/services/api", () => ({
  apiClient: {
    excludePrintJobObject: (printerId: string, name: string) =>
      mockExcludePrintJobObject(printerId, name),
    homePrinter: (printerId: string) => mockHomePrinter(printerId),
    homeXY: (printerId: string) => mockHomeXY(printerId),
    homeZ: (printerId: string) => mockHomeZ(printerId),
    movePrinter: (printerId: string, move: unknown) =>
      mockMovePrinter(printerId, move),
    movePrinterTo: (printerId: string, move: unknown) =>
      mockMovePrinterTo(printerId, move),
  },
}));

vi.mock("@/common/hooks/usePrinterDisplay", () => ({
  usePrinterDisplay: (printer: Printer) => printer,
}));

vi.mock("@/common/hooks/useSpoolmanConfigured", () => ({
  useSpoolmanConfigured: () => ({ ready: false }),
}));

vi.mock("@/features/printers/hooks/useAutoDispatch", () => ({
  useAutoDispatchStatus: () => ({ data: undefined }),
}));

vi.mock("@/features/printers/components/PrinterHistoryModal", () => ({
  PrinterHistoryModal: () => null,
}));

vi.mock("@/features/printers/components/PrinterFilesModal", () => ({
  PrinterFilesModal: () => null,
}));

vi.mock("@/features/printers/components/SpoolPickerModal", () => ({
  SpoolPickerModal: () => null,
}));

const printer: Printer = {
  id: "printer-1",
  name: "Printer Alpha",
  manufacturerName: "Prusa",
  modelName: "MK4",
  backend: PrinterBackend.PrusaLink,
  isOnline: true,
  backendUrl: "http://printer.local",
  isReachable: true,
  isEnabled: true,
  state: "Idle",
  hotendTemp: 25,
  hotendTarget: 0,
  bedTemp: 23,
  bedTarget: 0,
  x: 0,
  y: 0,
  z: 0,
};

function capabilities(
  overrides: Partial<PrinterBackendCapabilitiesDto> = {},
): PrinterBackendCapabilitiesDto {
  return {
    printerId: printer.id,
    printerName: printer.name,
    backend: printer.backend,
    supportsCamera: true,
    supportsFileDownload: true,
    supportsFileList: true,
    supportsFileUpload: true,
    supportsStartPrint: true,
    supportsControlOperations: true,
    supportsFileMetadata: true,
    supportsMovement: true,
    supportsTemperatureControl: true,
    supportsPrinterInformation: true,
    supportsHistory: true,
    supportsFilamentControl: false,
    supportsObjectExclusion: false,
    ...overrides,
  };
}

describe("PrinterDetailsSidebar", () => {
  beforeEach(() => {
    capturedStatisticsQueryOptions = undefined;
    mockStatisticsData = undefined;
    mockPrintJobObjectsData = undefined;
    mockVersionData = undefined;
    mockInvalidateQueries.mockClear();
    mockRefetch.mockClear();
    mockPrintJobObjectsRefetch.mockClear();
    mockSetQueryData.mockClear();
    mockExcludePrintJobObject.mockReset();
    mockExcludePrintJobObject.mockResolvedValue({
      success: true,
      message: "Object skipped",
    });
    mockHomePrinter.mockReset();
    mockHomeXY.mockReset();
    mockHomeZ.mockReset();
    mockMovePrinter.mockReset();
    mockToastError.mockReset();
    localStorage.clear();
    for (const command of [
      mockHomePrinter,
      mockHomeXY,
      mockHomeZ,
      mockMovePrinter,
      mockMovePrinterTo,
    ]) {
      command.mockReset().mockResolvedValue({ success: true });
    }
  });

  it.each([PrinterBackend.Moonraker, PrinterBackend.PrusaLink])(
    "routes %s Home All/XY/Z, jog, GO and Enter through direct commands",
    async (backend) => {
      localStorage.setItem("auth-token", crypto.randomUUID());
      const moonraker = {
        ...printer,
        id: "11111111-1111-4111-8111-111111111111",
        backend,
      };
      const auth = {
        isAuthenticated: true,
        user: { id: "sidebar-user" },
        hasRole: () => false,
        hasPermission: () => false,
      } as unknown as AuthContextType;
      render(
        <AuthContext.Provider value={auth}>
          <PrinterDetailsSidebar
            printerId={moonraker.id}
            printer={moonraker}
            backendCapabilities={capabilities({ backend })}
            onClose={vi.fn()}
          />
        </AuthContext.Provider>,
      );
      for (const [title, command] of [
        ["Home all axes", mockHomePrinter],
        ["Home X/Y", mockHomeXY],
        ["Home Z", mockHomeZ],
      ] as const) {
        await waitFor(() => expect(screen.getByTitle(title)).toBeEnabled());
        fireEvent.click(screen.getByTitle(title));
        await waitFor(() =>
          expect(command).toHaveBeenLastCalledWith(moonraker.id),
        );
      }
      await waitFor(() =>
        expect(
          screen.getByRole("button", { name: "Jog Y positive" }),
        ).toBeEnabled(),
      );
      fireEvent.click(screen.getByRole("button", { name: "Jog Y positive" }));
      await waitFor(() =>
        expect(mockMovePrinter).toHaveBeenLastCalledWith(moonraker.id, {
          y: 10,
        }),
      );
      await waitFor(() =>
        expect(screen.getByLabelText("X absolute target")).toBeEnabled(),
      );
      fireEvent.change(screen.getByLabelText("X absolute target"), {
        target: { value: "110" },
      });
      fireEvent.change(screen.getByLabelText("Y absolute target"), {
        target: { value: "120" },
      });
      expect(screen.getByTitle("Move to entered coordinates")).toBeDisabled();
      fireEvent.keyDown(screen.getByLabelText("X absolute target"), {
        key: "Enter",
      });
      expect(mockMovePrinterTo).not.toHaveBeenCalled();
      fireEvent.change(screen.getByLabelText("Z absolute target"), {
        target: { value: "10" },
      });
      fireEvent.click(screen.getByTitle("Move to entered coordinates"));
      await waitFor(() =>
        expect(mockMovePrinterTo).toHaveBeenLastCalledWith(moonraker.id, {
          x: 110,
          y: 120,
          z: 10,
        }),
      );
      await waitFor(() =>
        expect(screen.getByLabelText("Z absolute target")).toBeEnabled(),
      );
      fireEvent.change(screen.getByLabelText("Z absolute target"), {
        target: { value: "15" },
      });
      fireEvent.keyDown(screen.getByLabelText("Z absolute target"), {
        key: "Enter",
      });
      await waitFor(() =>
        expect(mockMovePrinterTo).toHaveBeenLastCalledWith(moonraker.id, {
          x: 110,
          y: 120,
          z: 15,
        }),
      );
      expect(mockMovePrinterTo).toHaveBeenCalledTimes(2);
      expect(
        screen.queryByText(/Motion Ready|Operation ID/i),
      ).not.toBeInTheDocument();
    },
  );

  it("bounds content layout on desktop and lets the inner region scroll", () => {
    const { container } = render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="content"
      />,
    );

    const shell = container.firstElementChild;
    expect(shell).toHaveClass(
      "w-full",
      "max-w-sm",
      "overflow-hidden",
      "flex",
      "flex-col",
    );
    expect(shell).toHaveClass("lg:max-h-[calc(100dvh-5rem)]", "lg:min-h-0");

    const scrollRegion = shell?.querySelector(".overflow-y-auto");
    expect(scrollRegion).toHaveClass("flex-1", "min-h-0", "overflow-y-auto");
  });

  it("keeps the drawer layout height contract unchanged", () => {
    const { container } = render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="panel"
      />,
    );

    const shell = container.firstElementChild;
    expect(shell).toHaveClass(
      "w-[calc(100%-1.5rem)]",
      "h-[calc(100%-1.5rem)]",
      "shrink-0",
    );
    expect(shell).not.toHaveClass("lg:max-h-[calc(100dvh-5rem)]");
  });

  it("renders the manufacturer and model in the subtitle when metadata is known", () => {
    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="content"
      />,
    );

    expect(screen.getByText("Prusa MK4")).toBeInTheDocument();
  });

  it('collapses the backend "Unknown" / "Unknown Model" sentinel pair into a single coherent subtitle', () => {
    const unknownPrinter: Printer = {
      ...printer,
      manufacturerName: "Unknown",
      modelName: "Unknown Model",
    };

    render(
      <PrinterDetailsSidebar
        printerId={unknownPrinter.id}
        printer={unknownPrinter}
        onClose={vi.fn()}
        layout="content"
      />,
    );

    expect(screen.getByText("Unknown model")).toBeInTheDocument();
    expect(screen.queryByText(/unknown.*unknown/i)).not.toBeInTheDocument();
  });

  it("uses the dedicated success action contract for move-to", () => {
    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        backendCapabilities={capabilities()}
        onClose={vi.fn()}
        layout="panel"
      />,
    );

    const moveToButton = screen.getByTitle("Move to entered coordinates");
    expect(moveToButton).toHaveAttribute("data-pf-variant", "success");
    expect(moveToButton).toHaveClass(
      "bg-[var(--pf-button-success-bg)]",
      "enabled:hover:bg-[var(--pf-button-success-hover)]",
      "text-[var(--pf-button-success-text)]",
      "border-[var(--pf-button-success-border)]",
      "enabled:hover:scale-105",
      "enabled:hover:shadow-md",
    );
    expect(moveToButton).not.toHaveClass(
      "bg-pf-success!",
      "text-[var(--pf-text-inverse)]!",
      "hover:bg-pf-success!",
      "hover:bg-pf-success-hover!",
    );
  });

  it("does not retry printer statistics query on client errors", () => {
    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="panel"
      />,
    );

    expect(capturedStatisticsQueryOptions?.retry).toBeTypeOf("function");
    const retry = capturedStatisticsQueryOptions!.retry as (
      failureCount: number,
      error: unknown,
    ) => boolean;
    expect(retry(0, { statusCode: 404 })).toBe(false);
    expect(retry(0, { response: { status: 404 } })).toBe(false);
    expect(retry(0, { statusCode: 500 })).toBe(true);
    expect(retry(2, { statusCode: 500 })).toBe(false);
  });

  it("renders never-synced statistics with an em dash last sync", () => {
    mockStatisticsData = {
      id: printer.id,
      printerId: printer.id,
      totalPrintHours: 0,
      totalJobsCompleted: 0,
      totalJobsFailed: 0,
      totalFilamentUsedGrams: 0,
      totalFilamentUsedMeters: 0,
      lastSyncTime: "0001-01-01T00:00:00",
      createdAt: "0001-01-01T00:00:00",
      updatedAt: "0001-01-01T00:00:00",
    };

    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="panel"
      />,
    );

    fireEvent.click(screen.getByText("Statistics"));

    const lastSyncTerm = screen.getByText("Last sync");
    const lastSyncRow = lastSyncTerm.closest("div");

    expect(lastSyncRow).not.toBeNull();
    expect(within(lastSyncRow!).getByText("—")).toBeInTheDocument();
  });

  it("labels the firmware reading as unrecorded when no recorded identity is returned (#1656)", () => {
    mockVersionData = {
      firmwareVersion: "1.2.3",
      backendVersion: "4.5.6",
      apiVersion: "7.8.9",
      supported: true,
      message: "",
    };

    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="panel"
      />,
    );

    expect(
      screen.getByText("No recorded firmware identity"),
    ).toBeInTheDocument();
    expect(
      screen.queryByText("Recorded firmware identity"),
    ).not.toBeInTheDocument();
  });

  it("labels the firmware reading as recorded when the version endpoint returns an identity (#1656)", () => {
    mockVersionData = {
      firmwareVersion: "1.2.3",
      backendVersion: "4.5.6",
      apiVersion: "7.8.9",
      supported: true,
      message: "",
      recordedFirmwareIdentity: {
        family: "Klipper",
        gcodeDialect: "Klipper",
        detectionSource: "printer",
        version: "1.2.3",
        detectionVersion: "moonraker-printer-info-v1",
        detectionConfidence: 1,
        detectedAtUtc: "2024-01-01T00:00:00.000Z",
        verified: false,
      },
    };

    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="panel"
      />,
    );

    expect(screen.getByText("Recorded firmware identity")).toBeInTheDocument();
    expect(
      screen.queryByText("No recorded firmware identity"),
    ).not.toBeInTheDocument();
  });

  it("hides object skip controls when backend capability is false", () => {
    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={{ ...printer, state: "Printing" }}
        backendCapabilities={capabilities({ supportsObjectExclusion: false })}
        onClose={vi.fn()}
        layout="panel"
      />,
    );

    expect(screen.queryByText("Objects")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Skip object cube")).not.toBeInTheDocument();
  });

  it("calls the skip object mutation after confirmation", async () => {
    mockPrintJobObjectsData = {
      printerId: printer.id,
      jobName: "plate.gcode",
      objects: [{ name: "cube", isExcluded: false, isCurrent: true }],
    };

    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={{
          ...printer,
          backend: PrinterBackend.Moonraker,
          state: "Printing",
        }}
        backendCapabilities={capabilities({
          backend: PrinterBackend.Moonraker,
          supportsObjectExclusion: true,
        })}
        onClose={vi.fn()}
        layout="panel"
      />,
    );

    fireEvent.click(screen.getByLabelText("Skip object cube"));
    fireEvent.click(screen.getByRole("button", { name: "Skip object" }));

    await waitFor(() => {
      expect(mockExcludePrintJobObject).toHaveBeenCalledWith(
        printer.id,
        "cube",
      );
    });

    expect(mockSetQueryData).toHaveBeenCalledWith(
      ["printers", printer.id, "printjob", "objects"],
      expect.any(Function),
    );
    const updateCache = mockSetQueryData.mock.calls[0][1] as (
      old: PrintJobObjectListDto,
    ) => PrintJobObjectListDto;
    expect(updateCache(mockPrintJobObjectsData!).objects[0]).toMatchObject({
      name: "cube",
      isExcluded: true,
      isCurrent: false,
    });
  });

  it("enables object skipping while a print is paused", async () => {
    mockPrintJobObjectsData = {
      printerId: printer.id,
      jobName: "plate.gcode",
      objects: [{ name: "cube", isExcluded: false, isCurrent: true }],
    };

    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={{
          ...printer,
          backend: PrinterBackend.Moonraker,
          state: "Paused",
        }}
        backendCapabilities={capabilities({
          backend: PrinterBackend.Moonraker,
          supportsObjectExclusion: true,
        })}
        onClose={vi.fn()}
        layout="panel"
      />,
    );

    fireEvent.click(screen.getByLabelText("Skip object cube"));
    fireEvent.click(screen.getByRole("button", { name: "Skip object" }));

    await waitFor(() => {
      expect(mockExcludePrintJobObject).toHaveBeenCalledWith(
        printer.id,
        "cube",
      );
    });
  });

  describe("Move controls while Klippy is shutdown (#1909)", () => {
    it("disables Home and jog controls when the printer reports a shutdown state", () => {
      render(
        <PrinterDetailsSidebar
          printerId={printer.id}
          printer={{
            ...printer,
            backend: PrinterBackend.Moonraker,
            state: "Shutdown",
          }}
          backendCapabilities={capabilities({
            backend: PrinterBackend.Moonraker,
          })}
          onClose={vi.fn()}
          layout="panel"
        />,
      );

      expect(screen.getByTitle("Home all axes")).toBeDisabled();
      expect(screen.getByTitle("Home X/Y")).toBeDisabled();
      expect(screen.getByTitle("Home Z")).toBeDisabled();
      expect(screen.getByTitle("Move to entered coordinates")).toBeDisabled();
    });

    it("enables idle online Moonraker controls without fetching tracking status", () => {
      render(
        <PrinterDetailsSidebar
          printerId={printer.id}
          printer={{
            ...printer,
            backend: PrinterBackend.Moonraker,
            state: "Idle",
          }}
          backendCapabilities={capabilities({
            backend: PrinterBackend.Moonraker,
          })}
          onClose={vi.fn()}
          layout="panel"
        />,
      );

      expect(screen.getByTitle("Home all axes")).toBeEnabled();
      expect(screen.getByTitle("Home X/Y")).toBeEnabled();
      expect(screen.getByTitle("Home Z")).toBeEnabled();
    });

    it("preserves the error toast for direct Home failures", async () => {
      localStorage.setItem("auth-token", crypto.randomUUID());
      const auth = {
        isAuthenticated: true,
        user: { id: "sidebar-legacy-user" },
        hasRole: () => false,
        hasPermission: () => false,
      } as unknown as AuthContextType;
      mockHomePrinter.mockRejectedValue(
        new Error("reconciliation is required"),
      );

      render(
        <AuthContext.Provider value={auth}>
          <PrinterDetailsSidebar
            printerId={printer.id}
            printer={{
              ...printer,
              backend: PrinterBackend.PrusaLink,
              state: "Idle",
            }}
            backendCapabilities={capabilities({
              backend: PrinterBackend.PrusaLink,
            })}
            onClose={vi.fn()}
            layout="panel"
          />
        </AuthContext.Provider>,
      );

      await waitFor(() =>
        expect(screen.getByTitle("Home all axes")).toBeEnabled(),
      );
      fireEvent.click(screen.getByTitle("Home all axes"));

      await waitFor(() => {
        expect(mockHomePrinter).toHaveBeenCalledWith(printer.id);
      });
      await waitFor(() => {
        expect(mockToastError).toHaveBeenCalled();
      });
    });
  });
});
