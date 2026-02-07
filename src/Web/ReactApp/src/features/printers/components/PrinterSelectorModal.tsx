import React, { useMemo, useState } from 'react';
import { SearchIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { Modal } from '@/common/components/modals/Modal';
import { PrinterImage } from '@/common/components/PrinterImage';

interface PrinterItem {
    id: string;
    name: string;
    modelName?: string;
    manufacturerName?: string;
    isOnline?: boolean;
    nozzleDiameter?: number;
    motionType?: number;
}

interface PrinterSelectorModalProps {
    isOpen: boolean;
    printers: PrinterItem[];
    onSelect?: (printerId: string) => void;
    onSelectMany?: (printerIds: string[]) => void;
    onClose: () => void;
    selectedPrinterId?: string;
    selectedPrinterIds?: string[];
    multiSelect?: boolean;
    title?: string;
    confirmLabel?: string;

    overwriteExisting?: boolean;
    onOverwriteExistingChange?: (value: boolean) => void;
}

export function PrinterSelectorModal({
    isOpen,
    printers,
    onSelect,
    onSelectMany,
    onClose,
    selectedPrinterId,
    selectedPrinterIds,
    multiSelect = false,
    title,
    confirmLabel,
    overwriteExisting,
    onOverwriteExistingChange
}: PrinterSelectorModalProps) {
    const [searchText, setSearchText] = useState('');
    const [localSelectedIds, setLocalSelectedIds] = useState<string[]>(selectedPrinterIds ?? []);

    const handleClose = () => {
        setSearchText('');
        setLocalSelectedIds(selectedPrinterIds ?? []);
        onClose();
    };

    const filteredPrinters = useMemo(() => {
        if (!searchText.trim()) return printers;
        const search = searchText.toLowerCase();
        return printers.filter(p =>
            p.name.toLowerCase().includes(search) ||
            p.modelName?.toLowerCase().includes(search) ||
            p.manufacturerName?.toLowerCase().includes(search)
        );
    }, [printers, searchText]);

    const handleSelect = (printerId: string) => {
        if (multiSelect) {
            setLocalSelectedIds((prev) => {
                if (prev.includes(printerId)) return prev.filter((id) => id !== printerId);
                return [...prev, printerId];
            });
            return;
        }

        onSelect?.(printerId);
        handleClose();
    };

    const handleConfirm = () => {
        onSelectMany?.(localSelectedIds);
        handleClose();
    };

    {/* Search */}
    const searchContent = (
        <div className="px-6 pt-4 pb-2">
            <div className="relative">
                <SearchIcon className="absolute left-3 top-1/2 transform -translate-y-1/2 w-5 h-5 text-pf-text-secondary" />
                <input
                    type="text"
                    placeholder="Search printers..."
                    value={searchText}
                    onChange={(e) => setSearchText(e.target.value)}
                    className="w-full pl-10 pr-4 py-2 bg-pf-bg-1 border border-pf-border rounded-lg text-pf-text placeholder-pf-text-secondary focus:outline-hidden focus:ring-2 focus:ring-pf-accent"
                />
            </div>
        </div>
    );

    const optionsContent =
        multiSelect && typeof overwriteExisting === 'boolean' && onOverwriteExistingChange ? (
            <div className="px-6 pb-2">
                <Checkbox
                    label="Overwrite existing schedules"
                    checked={overwriteExisting}
                    onChange={(e) => onOverwriteExistingChange(e.target.checked)}
                />
            </div>
        ) : null;

    const printerGrid = (
        <div className="flex-1 overflow-y-auto p-6">
            {filteredPrinters.length === 0 ? (
                <div className="flex items-center justify-center h-full text-pf-text-muted">
                    No printers found
                </div>
            ) : (
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
                    {filteredPrinters.map((printer) => {
                        const isSelected = multiSelect
                            ? localSelectedIds.includes(printer.id)
                            : printer.id === selectedPrinterId;

                        return (
                            <Button
                                type="button"
                                key={printer.id}
                                variant={isSelected ? 'primary' : 'secondary'}
                                onClick={() => handleSelect(printer.id)}
                                className="group relative overflow-hidden h-auto p-0 !rounded-lg !justify-start"
                                aria-pressed={multiSelect ? isSelected : undefined}
                            >
                                {/* Cover Image (uses fallback SVG if no model image or on error) */}
                                <div className="relative w-full h-40 overflow-hidden bg-pf-bg-1">
                                    <PrinterImage
                                        manufacturerName={printer.manufacturerName}
                                        modelName={printer.modelName}
                                        motionType={printer.motionType}
                                        alt={printer.name}
                                        className="w-full h-full object-cover group-hover:scale-105 transition-transform duration-200"
                                    />
                                </div>

                                {/* Info */}
                                <div className="p-4">
                                    <h3 className="font-semibold text-pf-text text-lg truncate text-left">
                                        {printer.name}
                                    </h3>

                                    {(printer.modelName || printer.nozzleDiameter) && (
                                        <p className="text-sm text-pf-text-secondary truncate text-left">
                                            {printer.manufacturerName && `${printer.manufacturerName} • `}
                                            {printer.modelName}
                                            {printer.modelName && printer.nozzleDiameter && ' • '}
                                            {printer.nozzleDiameter && `${printer.nozzleDiameter}mm nozzle`}
                                        </p>
                                    )}

                                    {/* Status Badge */}
                                    <div className="mt-3 flex items-center justify-between">
                                        <span
                                            className={`inline-block px-2 py-1 rounded text-xs font-semibold uppercase tracking-wide ${printer.isOnline
                                                    ? 'bg-pf-success/20 text-pf-success'
                                                    : 'bg-pf-status-offline-bg text-pf-status-offline-text'
                                                }`}
                                        >
                                            {printer.isOnline ? 'Online' : 'Offline'}
                                        </span>

                                        {isSelected && (
                                            <span className="inline-block px-2 py-1 rounded-sm text-xs font-semibold uppercase tracking-wide bg-pf-accent/20 text-pf-accent">
                                                Selected
                                            </span>
                                        )}
                                    </div>
                                </div>
                            </Button>
                        );
                    })}
                </div>
            )}
        </div>
    );

    return (
        <Modal
            isOpen={isOpen}
            onClose={handleClose}
            title={title ?? (multiSelect ? 'Select Printers' : 'Select Printer')}
            width="max-w-4xl"
            footer={
                multiSelect ? (
                    <>
                        <Button type="button" variant="subtle" onClick={handleClose}>
                            Cancel
                        </Button>
                        <Button
                            type="button"
                            variant="primary"
                            onClick={handleConfirm}
                            disabled={localSelectedIds.length === 0}
                        >
                            {confirmLabel ?? 'Confirm'}
                        </Button>
                    </>
                ) : undefined
            }
        >
            {searchContent}
            {optionsContent}
            {printerGrid}
        </Modal>
    );
}
