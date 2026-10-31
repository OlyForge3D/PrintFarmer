import '@testing-library/jest-dom';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { MaintenanceReport } from '../MaintenanceReport';
import { useMaintenanceTrends } from '../../hooks/useMaintenanceTrends';

vi.mock('../../hooks/useMaintenanceTrends', () => ({
  useMaintenanceTrends: vi.fn(),
}));

const autoTableMock = vi.fn();
const textMock = vi.fn();
const saveMock = vi.fn();

// jsPDF is a named export; jspdf-autotable is a side-effect import that
// patches jsPDF.prototype.autoTable. Both are lazy-loaded via `import()`
// inside the export handler (issue #1241), so the mocks below mirror the
// real module shapes to prove the dynamic-import interop is correct.
vi.mock('jspdf', () => ({
  jsPDF: vi.fn().mockImplementation(function (this: Record<string, unknown>) {
    this.text = textMock;
    this.autoTable = autoTableMock;
    this.save = saveMock;
  }),
}));

vi.mock('jspdf-autotable', () => ({}));

const mockedUseMaintenanceTrends = vi.mocked(useMaintenanceTrends);

describe('MaintenanceReport', () => {
  let objectUrlDescriptor: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.clearAllMocks();
    // CSVLink creates an object URL on render; these tests cover PDF export,
    // not jsdom's Blob/URL implementation.
    objectUrlDescriptor = Object.getOwnPropertyDescriptor(window.URL, 'createObjectURL');
    Object.defineProperty(window.URL, 'createObjectURL', {
      configurable: true,
      writable: true,
      value: vi.fn(() => 'blob:maintenance-report'),
    });
  });

  afterEach(() => {
    if (objectUrlDescriptor) {
      Object.defineProperty(window.URL, 'createObjectURL', objectUrlDescriptor);
    } else {
      Reflect.deleteProperty(window.URL, 'createObjectURL');
    }
  });

  it('does not eagerly load jspdf on render', () => {
    mockedUseMaintenanceTrends.mockReturnValue({
      data: [{ date: '2024-01-01', printer: 'P1', component: 'Nozzle', action: 'Replace', cost: 10 }],
      isLoading: false,
      error: null,
    } as ReturnType<typeof useMaintenanceTrends>);

    render(<MaintenanceReport />);

    expect(screen.getByText('Export PDF')).toBeInTheDocument();
    expect(textMock).not.toHaveBeenCalled();
    expect(autoTableMock).not.toHaveBeenCalled();
  });

  it('dynamically imports jspdf and jspdf-autotable and generates the PDF on export click', async () => {
    mockedUseMaintenanceTrends.mockReturnValue({
      data: [{ date: '2024-01-01', printer: 'P1', component: 'Nozzle', action: 'Replace', cost: 10 }],
      isLoading: false,
      error: null,
    } as ReturnType<typeof useMaintenanceTrends>);

    const user = userEvent.setup();
    render(<MaintenanceReport />);

    await user.click(screen.getByText('Export PDF'));

    // exportPDF is async and awaits the dynamic import() chain before
    // touching jsPDF, so the click's returned promise resolves after
    // userEvent's own act/flush — assertions must wait for it (Hicks #1:
    // asserting immediately here is a race and fails most runs).
    await waitFor(() => expect(saveMock).toHaveBeenCalledWith('maintenance-report.pdf'));

    expect(textMock).toHaveBeenCalledWith('Maintenance Report', 14, 16);
    expect(autoTableMock).toHaveBeenCalledWith(
      expect.objectContaining({
        head: [['Date', 'Printer', 'Component', 'Action', 'Cost']],
        startY: 22,
      })
    );
  });

  it('cycles numeric cost and chronological date ordering while preserving export order', async () => {
    const rows = [
      { date: '2026-10-01', printer: 'Alpha', component: 'Nozzle', action: 'Replace', cost: 10 },
      { date: '2026-02-01', printer: 'Beta', component: 'Nozzle', action: 'Replace', cost: 2 },
      { date: '2026-12-01', printer: 'Zulu', component: 'Nozzle', action: 'Replace', cost: 100 },
    ];
    mockedUseMaintenanceTrends.mockReturnValue({
      data: rows, isLoading: false, error: null,
    } as ReturnType<typeof useMaintenanceTrends>);
    const user = userEvent.setup();
    render(<MaintenanceReport />);
    const names = () => within(screen.getByRole('table')).getAllByRole('row').slice(1)
      .map(row => row.children[1].textContent);
    const cost = screen.getByRole('button', { name: 'Cost' });
    await user.click(cost);
    expect(names()).toEqual(['Beta', 'Alpha', 'Zulu']);
    await user.keyboard('{Enter}');
    expect(names()).toEqual(['Zulu', 'Alpha', 'Beta']);
    await user.keyboard(' ');
    expect(names()).toEqual(['Alpha', 'Beta', 'Zulu']);
    expect(cost.closest('th')).toHaveAttribute('aria-sort', 'none');
    await user.click(screen.getByRole('button', { name: 'Date' }));
    expect(names()).toEqual(['Beta', 'Alpha', 'Zulu']);
    await user.click(screen.getByText('Export PDF'));
    await waitFor(() => expect(saveMock).toHaveBeenCalled());
    expect(autoTableMock).toHaveBeenCalledWith(expect.objectContaining({
      body: rows.map(row => [row.date, row.printer, row.component, row.action, row.cost]),
    }));
  });
});
