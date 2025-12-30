import React, { useState, useEffect } from 'react';
import { Location, locationService } from '@/services/locationService';
import { Printer, printerLocationService } from '@/services/printerLocationService';

export const PrinterLocationDragDrop: React.FC = () => {
  const [locations, setLocations] = useState<Location[]>([]);
  const [printers, setPrinters] = useState<Printer[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [draggedPrinter, setDraggedPrinter] = useState<Printer | null>(null);

  // Load data on mount
  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    try {
      setLoading(true);
      setError(null);
      const [locationsData, printersData] = await Promise.all([
        locationService.getAllLocations(),
        printerLocationService.getAllPrinters(),
      ]);
      setLocations(locationsData);
      setPrinters(printersData);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load data');
    } finally {
      setLoading(false);
    }
  };

  const getUnassignedPrinters = () => {
    return printers.filter((p) => !p.locationId);
  };

  const getPrintersInLocation = (locationId: string) => {
    return printers.filter((p) => p.locationId === locationId);
  };

  const handleDragStart = (e: React.DragEvent<HTMLDivElement>, printer: Printer) => {
    setDraggedPrinter(printer);
    e.dataTransfer.effectAllowed = 'move';
  };

  const handleDragOver = (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
  };

  const handleDropOnLocation = async (e: React.DragEvent<HTMLDivElement>, locationId: string) => {
    e.preventDefault();
    if (!draggedPrinter) return;

    try {
      setError(null);
      await printerLocationService.assignPrinterToLocation(draggedPrinter.id, locationId);
      
      // Update local state
      setPrinters(
        printers.map((p) =>
          p.id === draggedPrinter.id ? { ...p, locationId } : p
        )
      );
      setDraggedPrinter(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to assign printer to location');
      setDraggedPrinter(null);
    }
  };

  const handleDropOnUnassigned = async (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    if (!draggedPrinter || !draggedPrinter.locationId) return;

    try {
      setError(null);
      await printerLocationService.unassignPrinterFromLocation(draggedPrinter.id);
      
      // Update local state
      setPrinters(
        printers.map((p) =>
          p.id === draggedPrinter.id ? { ...p, locationId: undefined } : p
        )
      );
      setDraggedPrinter(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to unassign printer');
      setDraggedPrinter(null);
    }
  };

  if (loading) {
    return <div className="p-6 text-center">Loading...</div>;
  }

  const unassignedPrinters = getUnassignedPrinters();

  return (
    <div className="space-y-6">
      {/* Header */}
      <div>
        <h1 className="text-3xl font-bold mb-2 text-pf-text-primary">Assign Printers to Locations</h1>
        <p className="text-pf-text-secondary">Drag and drop printers to assign them to locations</p>
      </div>

      {/* Error message */}
      {error && (
        <div className="bg-pf-error-bg border border-pf-error-border text-pf-error-text px-4 py-3 rounded-lg">
          {error}
        </div>
      )}

      {/* Main grid */}
      <div className="grid grid-cols-1 lg:grid-cols-4 gap-6">
        {/* Unassigned Printers Column */}
        <div className="lg:col-span-1">
          <div
            onDragOver={handleDragOver}
            onDrop={handleDropOnUnassigned}
            className="bg-pf-bg-2 border-2 border-dashed border-pf-border rounded-lg p-4 min-h-96 transition-colors hover:border-pf-accent"
          >
            <h2 className="text-lg font-semibold mb-4 text-pf-text-primary">
              Unassigned Printers ({unassignedPrinters.length})
            </h2>
            <div className="space-y-2">
              {unassignedPrinters.length === 0 ? (
                <p className="text-pf-text-tertiary text-center py-8">All printers assigned</p>
              ) : (
                unassignedPrinters.map((printer) => (
                  <PrinterCard
                    key={printer.id}
                    printer={printer}
                    onDragStart={(e) => handleDragStart(e, printer)}
                    isDragging={draggedPrinter?.id === printer.id}
                  />
                ))
              )}
            </div>
          </div>
        </div>

        {/* Locations Columns */}
        <div className="lg:col-span-3 grid grid-cols-1 md:grid-cols-3 gap-6">
          {locations.length === 0 ? (
            <div className="col-span-full bg-pf-bg-2 border border-pf-warning rounded-lg p-4">
              <p className="text-pf-warning-text">No locations created yet. Create a location first.</p>
            </div>
          ) : (
            locations.map((location) => {
              const locationPrinters = getPrintersInLocation(location.id);
              return (
                <div
                  key={location.id}
                  onDragOver={handleDragOver}
                  onDrop={(e) => handleDropOnLocation(e, location.id)}
                  className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 shadow hover:border-pf-primary transition-colors"
                >
                  <h3 className="text-lg font-semibold mb-2 text-pf-text-primary">{location.name}</h3>
                  {location.description && (
                    <p className="text-sm text-pf-text-secondary mb-3">{location.description}</p>
                  )}
                  <div className="bg-pf-status-online-bg rounded px-2 py-1 mb-3 inline-block">
                    <span className="text-sm font-medium text-pf-status-online-text">
                      {locationPrinters.length} printers
                    </span>
                  </div>
                  <div className="space-y-2 min-h-32 bg-pf-bg-2 rounded-md p-3">
                    {locationPrinters.length === 0 ? (
                      <p className="text-pf-text-tertiary text-center py-8 text-sm">Drag printers here</p>
                    ) : (
                      locationPrinters.map((printer) => (
                        <PrinterCard
                          key={printer.id}
                          printer={printer}
                          onDragStart={(e) => handleDragStart(e, printer)}
                          isDragging={draggedPrinter?.id === printer.id}
                        />
                      ))
                    )}
                  </div>
                </div>
              );
            })
          )}
        </div>
      </div>
    </div>
  );
};

interface PrinterCardProps {
  printer: Printer;
  onDragStart: (e: React.DragEvent<HTMLDivElement>) => void;
  isDragging: boolean;
}

const PrinterCard: React.FC<PrinterCardProps> = ({ printer, onDragStart, isDragging }) => {
  return (
    <div
      draggable
      onDragStart={onDragStart}
      className={`
        bg-pf-bg-1 border rounded-md p-3 cursor-move transition-all shadow-sm
        ${isDragging ? 'opacity-50 scale-95 border-pf-accent' : 'border-pf-border hover:shadow-md hover:border-pf-primary'}
      `}
    >
      <p className="font-medium text-sm text-pf-text-primary truncate">{printer.name}</p>
      <p className="text-xs text-pf-text-tertiary truncate">{printer.serverUrl}</p>
    </div>
  );
};

export default PrinterLocationDragDrop;
