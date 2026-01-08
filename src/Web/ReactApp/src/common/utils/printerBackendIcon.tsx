import React from 'react';
import { PrinterBackend } from '@/types/api';
import moonrakerIcon from '@/assets/moonraker.svg';
import prusalinkIcon from '@/assets/prusalink.svg';
import octoprintIcon from '@/assets/octoprint.svg';

/**
 * Get the backend icon component for a printer backend type.
 * Returns the appropriate icon (image or emoji) for the given backend.
 *
 * @param backend The backend type as PrinterBackend enum, number, or string
 * @returns A React element containing the backend icon
 */
export function getBackendIcon(backend: PrinterBackend | number | string) {
  let backendValue: PrinterBackend | undefined = undefined;

  // Handle numeric values
  if (typeof backend === 'number') {
    backendValue = backend;
  }
  // Handle string values (case-insensitive)
  else if (typeof backend === 'string') {
    switch (backend.toLowerCase()) {
      case 'moonraker':
        backendValue = PrinterBackend.Moonraker;
        break;
      case 'prusalink':
        backendValue = PrinterBackend.PrusaLink;
        break;
      case 'sdcp':
        backendValue = PrinterBackend.SDCP;
        break;
      case 'octoprint':
        backendValue = PrinterBackend.OctoPrint;
        break;
      default:
        backendValue = undefined;
    }
  }

  switch (backendValue) {
    case PrinterBackend.Moonraker:
      return (
        <img
          src={moonrakerIcon}
          alt="Moonraker"
          title="Moonraker"
          className="inline h-5 w-5 align-middle mr-1"
        />
      );
    case PrinterBackend.PrusaLink:
      return (
        <img
          src={prusalinkIcon}
          alt="PrusaLink"
          title="PrusaLink"
          className="inline h-5 w-5 align-middle mr-1"
        />
      );
    case PrinterBackend.SDCP:
      return (
        <span title="SDCP" aria-label="SDCP" role="img" className="mr-1">
          📡
        </span>
      );
    case PrinterBackend.OctoPrint:
      return (
        <img
          src={octoprintIcon}
          alt="OctoPrint"
          title="OctoPrint"
          className="inline h-5 w-5 align-middle mr-1"
        />
      );
    default:
      return (
        <span title="Other" aria-label="Other" role="img" className="mr-1">
          🖨️
        </span>
      );
  }
}
