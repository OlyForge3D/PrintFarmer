## Camera endpoint detection API

**Date:** 2026-05-26T09:45:35.148-07:00  
**By:** Lambert  
**Requested by:** Jeff Papiez

### Endpoint contract

- `POST /api/cameras/detect-endpoints`
- Request: `{ "printerId": "<guid>" }`
- Success response: `{ "streamUrl": string | null, "snapshotUrl": string | null, "detected": boolean, "source": string }`
- Missing printer: `404 { "message": "Printer not found" }`
- Unsupported backend or probe failure: `200` with `detected: false`
- JSON remains camelCase through the existing API serialization policy.

### Probe extension pattern

- `IPrinterCameraProbe` lives in `src/discovery/` so the API can depend on one discovery contract.
- Backend plugin projects register concrete probes through `IExtendedBackendPlugin.RegisterAdditionalServices()`.
- `PrinterCameraEndpointDetectionService` loads the printer, selects the probe by `PrinterBackend`, and catches probe failures as non-fatal detection misses.
- Concrete probes added now:
  - Moonraker/Klipper: queries configured Moonraker webcam URLs; API source is `klipper`.
  - OctoPrint: returns conventional `/webcam/?action=stream` and `/webcam/?action=snapshot` URLs.
  - SDCP/Elegoo: uses existing SDCP camera capability probes for `/video` and `/snapshot`.

### Camera DTO shape

`CameraDto` now carries `printerId` and `printerName` for associated cameras. `DisplayCameraDto` already had this shape, but regular list/get camera endpoints did not expose the printer name Ripley needs for the edit modal and table.

### Follow-up

TODO for Brett/Hudson/Ripley: add concrete probes for PrusaLink/Buddy companion cameras, FlashForge, and any future Bambu backend once the backend-specific camera contract is known. Unsupported backends currently return `detected: false` with the normalized backend source.
