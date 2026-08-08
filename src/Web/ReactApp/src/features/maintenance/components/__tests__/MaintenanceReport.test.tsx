import '@testing-library/jest-dom';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
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
  beforeEach(() => {
    vi.clearAllMocks();
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

    expect(textMock).toHaveBeenCalledWith('Maintenance Report', 14, 16);
    expect(autoTableMock).toHaveBeenCalledWith(
      expect.objectContaining({
        head: [['Date', 'Printer', 'Component', 'Action', 'Cost']],
        startY: 22,
      })
    );
    expect(saveMock).toHaveBeenCalledWith('maintenance-report.pdf');
  });
});
