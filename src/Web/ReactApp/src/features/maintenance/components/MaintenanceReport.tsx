import React, { useRef } from 'react';
import { useMaintenanceTrends } from '../hooks/useMaintenanceTrends';
import { CSVLink } from 'react-csv';
import jsPDF from 'jspdf';
import 'jspdf-autotable';

export const MaintenanceReport: React.FC = () => {
  const { data, isLoading, error } = useMaintenanceTrends();
  const rows = Array.isArray(data) ? data : [];
  const csvLinkRef = useRef<any>(null);

  if (isLoading) return <div>Loading report...</div>;
  if (error) return <div>Error loading report.</div>;
  if (!rows.length) return <div>No data available.</div>;

  const headers = [
    { label: 'Date', key: 'date' },
    { label: 'Printer', key: 'printer' },
    { label: 'Component', key: 'component' },
    { label: 'Action', key: 'action' },
    { label: 'Cost', key: 'cost' },
  ];

  const exportPDF = () => {
    const doc = new jsPDF();
    doc.text('Maintenance Report', 14, 16);
    // @ts-ignore
    doc.autoTable({
      head: [headers.map(h => h.label)],
      body: rows.map((row: any) => headers.map(h => row[h.key])),
      startY: 22,
    });
    doc.save('maintenance-report.pdf');
  };

  return (
    <section aria-labelledby="maintenance-report-title" className="mt-8">
      <h2 id="maintenance-report-title" className="text-lg font-semibold mb-2">Maintenance Report</h2>
      <div className="flex gap-2 mb-4">
        <button
          type="button"
          onClick={exportPDF}
          className="px-3 py-1 bg-blue-600 text-white rounded focus:outline focus-visible:ring"
        >
          Export PDF
        </button>
        <CSVLink
          data={data}
          headers={headers}
          filename="maintenance-report.csv"
          ref={csvLinkRef}
          className="px-3 py-1 bg-green-600 text-white rounded focus:outline focus-visible:ring"
          aria-label="Export CSV"
        >
          Export CSV
        </CSVLink>
      </div>
      <div className="overflow-x-auto">
        <table className="min-w-full border text-sm">
          <thead>
            <tr>
              {headers.map(h => (
                <th key={h.key} className="border px-2 py-1 bg-gray-100 text-left">{h.label}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row: any, i: number) => (
              <tr key={i}>
                {headers.map(h => (
                  <td key={h.key} className="border px-2 py-1">{row[h.key]}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
};

export default MaintenanceReport;
