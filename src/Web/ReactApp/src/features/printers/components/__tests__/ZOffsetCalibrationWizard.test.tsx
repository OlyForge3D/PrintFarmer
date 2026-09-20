import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ZOffsetCalibrationWizard } from "@/features/printers/components/ZOffsetCalibrationWizard";
import { PrinterBackend, type CommandResult, type Printer } from "@/types/api";

const mockHomePrinter = vi.fn();
const mockMovePrinterTo = vi.fn();
const mockSaveZOffset = vi.fn();
const mockToastError = vi.fn();
const mockToastSuccess = vi.fn();

vi.mock("@/services/api", () => ({
  apiClient: {
    homePrinter: (...args: unknown[]) => mockHomePrinter(...args),
    movePrinterTo: (...args: unknown[]) => mockMovePrinterTo(...args),
    saveZOffset: (...args: unknown[]) => mockSaveZOffset(...args),
  },
}));
vi.mock("sonner", () => ({
  toast: {
    error: (...args: unknown[]) => mockToastError(...args),
    success: (...args: unknown[]) => mockToastSuccess(...args),
  },
}));
vi.mock("@/common/hooks/useApi", () => ({
  queryKeys: { printers: ["printers"] },
}));
vi.mock("@/common/components/modals/Modal", () => ({
  Modal: ({
    isOpen,
    title,
    footer,
    children,
  }: {
    isOpen: boolean;
    title: string;
    footer?: React.ReactNode;
    children: React.ReactNode;
  }) =>
    isOpen ? (
      <div data-testid="modal">
        <h1>{title}</h1>
        {children}
        {footer}
      </div>
    ) : null,
}));

function createTestPrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: "calibration-printer",
    name: "Test Printer",
    backend: PrinterBackend.Moonraker,
    isOnline: true,
    isEnabled: true,
    inMaintenance: false,
    backendUrl: "http://printer.local",
    isReachable: true,
    state: "Idle",
    rowVersion: "printer-v1",
    ...overrides,
  };
}

function renderWizard(props: { isOpen?: boolean; printer?: Printer } = {}) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: 3 } },
  });
  const onClose = vi.fn();
  return {
    onClose,
    ...render(
      <QueryClientProvider client={client}>
        <ZOffsetCalibrationWizard
          isOpen={props.isOpen ?? true}
          onClose={onClose}
          printer={props.printer ?? createTestPrinter()}
        />
      </QueryClientProvider>,
    ),
  };
}

async function openAdjustment() {
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: /next/i }));
  await user.click(screen.getByRole("button", { name: /home all axes/i }));
  await user.click(
    await screen.findByRole("button", { name: /move to center/i }),
  );
  await screen.findByText(/Z-Offset:.*0\.000 mm/);
  return user;
}

describe("ZOffsetCalibrationWizard direct commands", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mockHomePrinter.mockResolvedValue({ success: true });
    mockMovePrinterTo.mockResolvedValue({ success: true });
    mockSaveZOffset.mockResolvedValue({ success: true });
  });

  it("does not render when closed", () => {
    renderWizard({ isOpen: false });
    expect(screen.queryByTestId("modal")).not.toBeInTheDocument();
  });

  it("renders introduction, progress, and an honest acceptance notice without tracking UI", () => {
    renderWizard();
    expect(screen.getByText(/this wizard will guide you/i)).toBeInTheDocument();
    expect(screen.getByRole("progressbar")).toBeInTheDocument();
    expect(
      screen.getByText(/not confirmed physically complete/i),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(/Motion Ready|Operation ID|Motion technical details/i),
    ).not.toBeInTheDocument();
  });

  it.each(["Moonraker", "PrusaLink", "OctoPrint", "FlashForge", "SDCP"])(
    "uses direct homing for %s and reports acceptance, not completion",
    async (backend) => {
      const user = userEvent.setup();
      renderWizard({
        printer: createTestPrinter({ backend: backend as Printer["backend"] }),
      });
      await user.click(screen.getByRole("button", { name: /next/i }));
      await user.click(screen.getByRole("button", { name: /home all axes/i }));
      await screen.findByText(/move the nozzle to the center/i);
      expect(mockHomePrinter).toHaveBeenCalledExactlyOnceWith(
        "calibration-printer",
      );
      expect(mockToastSuccess).toHaveBeenCalledWith(
        expect.stringContaining("Homing command accepted"),
      );
    },
  );

  it("keeps navigation blocked until the direct request settles without duplicate sends", async () => {
    let finish!: (result: CommandResult) => void;
    mockHomePrinter.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    renderWizard();
    fireEvent.click(screen.getByRole("button", { name: /next/i }));
    fireEvent.click(screen.getByRole("button", { name: /home all axes/i }));
    await waitFor(() => expect(mockHomePrinter).toHaveBeenCalledOnce());
    expect(screen.getByRole("button", { name: /please wait/i })).toBeDisabled();
    expect(screen.getByRole("button", { name: /back/i })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: /please wait/i }));
    expect(mockHomePrinter).toHaveBeenCalledOnce();
    await act(async () => finish({ success: true }));
    expect(
      screen.getByText(/move the nozzle to the center/i),
    ).toBeInTheDocument();
  });

  it.each(["rejected", "HTTP failure", "timeout", "aborted"])(
    "does not advance or retry after %s",
    async (failure) => {
      if (failure === "rejected")
        mockHomePrinter.mockResolvedValue({
          success: false,
          error: "Printer rejected command",
        });
      else mockHomePrinter.mockRejectedValue(new Error(failure));
      const user = userEvent.setup();
      renderWizard();
      await user.click(screen.getByRole("button", { name: /next/i }));
      await user.click(screen.getByRole("button", { name: /home all axes/i }));
      await waitFor(() => expect(mockToastError).toHaveBeenCalledOnce());
      expect(
        screen.getByRole("button", { name: /home all axes/i }),
      ).toBeEnabled();
      expect(
        screen.queryByText(/move the nozzle to the center/i),
      ).not.toBeInTheDocument();
      expect(mockHomePrinter).toHaveBeenCalledOnce();
      expect(mockMovePrinterTo).not.toHaveBeenCalled();
    },
  );

  it("navigates back to the introduction and cancels", async () => {
    const user = userEvent.setup();
    const { onClose } = renderWizard();
    await user.click(screen.getByRole("button", { name: /next/i }));
    await user.click(screen.getByRole("button", { name: /back/i }));
    expect(screen.getByText(/this wizard will guide you/i)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /cancel/i }));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it("moves to center, adjusts Z directly, and preserves the visual guide and save contract", async () => {
    renderWizard();
    const user = await openAdjustment();
    expect(mockMovePrinterTo).toHaveBeenCalledExactlyOnceWith(
      "calibration-printer",
      { x: 110, y: 110, z: 10, f: 3000 },
    );
    for (const text of [
      "0.01 mm",
      "0.05 mm",
      "0.1 mm",
      "First Layer Visual Guide",
      "Too Far",
      "Just Right",
      "Too Close",
    ]) {
      expect(screen.getByText(text)).toBeInTheDocument();
    }
    await user.click(screen.getByRole("button", { name: /nozzle down/i }));
    expect(mockMovePrinterTo).toHaveBeenLastCalledWith("calibration-printer", {
      z: 9.95,
      f: 300,
    });
    await user.click(screen.getByRole("button", { name: /looks good/i }));
    expect(screen.getByText(/ready to save/i)).toBeInTheDocument();
    expect(screen.getByText(/Klipper\/Moonraker/i)).toBeInTheDocument();
    expect(screen.getByText(/SET_GCODE_OFFSET/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /save z-offset/i }));
    await waitFor(() =>
      expect(mockSaveZOffset).toHaveBeenCalledExactlyOnceWith(
        "calibration-printer",
        { offsetMm: -0.05, saveToFirmware: true },
        "printer-v1",
      ),
    );
  });
});
