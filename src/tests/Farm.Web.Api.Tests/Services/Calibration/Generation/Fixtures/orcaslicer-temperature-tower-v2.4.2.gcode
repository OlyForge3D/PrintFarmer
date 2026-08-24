; PrintFarmer calibration golden fixture -- Temperature Tower (per-band nozzle setpoints)
;
; PINNED REFERENCE VERSION: OrcaSlicer v2.4.2
;   repository: https://github.com/OrcaSlicer/OrcaSlicer
;   commit:     8500fcdccaa10b5099ac20d252af3a7c560046f1
;   provenance: compliance/calibration-provenance.json -> approvedSources[id=orcaslicer-v2.4.2]
;               and referenceRecords[id=calibration-temperature-tower-orcaslicer-golden-fixture]
;
; PROVENANCE / EXECUTION-GAP NOTE (read before editing or bumping the pin):
;   This sandbox has no OrcaSlicer binary or GUI execution path -- there is no vendored engine
;   anywhere in this repository (checked: src/Slicers/Farm.Slicers.OrcaSlicer.v2_4_0 and v2_3_1
;   are metadata/UI wrapper projects only, not a runnable slicing engine) and no headless
;   `orca-slicer --slice` entry point is available. This fixture is therefore NOT a byte-for-byte
;   capture of a live OrcaSlicer temperature-tower slice.
;
;   Instead, it is a faithful, independently authored reconstruction of the one part of
;   OrcaSlicer's temperature-tower mechanism that is documented and stable across a slice: for
;   each tower band, OrcaSlicer issues a nozzle temperature setpoint using the ordinary firmware
;   M104 (set, non-blocking) / M109 (set-and-wait) commands before continuing to print that
;   band's walls (see src/libslic3r/calib.cpp and the Calib_Temp_Tower path in
;   src/slic3r/Utils/CalibUtils.cpp in the pinned commit above). Only this ordered per-band
;   M104/M109 setpoint sequence is captured and asserted here; the full tower toolpath/geometry
;   (travel moves, wall extrusion, infill) is NOT verified against real OrcaSlicer output, because
;   producing it requires slicing a real 3D model through OrcaSlicer's full GUI pipeline
;   (Model/Print/Plater), which cannot run in this environment. This gap is intentional and is
;   flagged for reviewer awareness per https://github.com/OlyForge3D/PrintFarmer/issues/1926 and
;   https://github.com/OlyForge3D/PrintFarmer/issues/1929.
;
; Sweep represented below: nozzle baseline 220C, start=240C, end=200C, step=5C (OrcaSlicer's
; documented default temperature-tower sweep is baseline +20C to baseline -20C in 5C decrements),
; 9 bands, strictly descending. This matches PrintFarmer's own default-derived sweep for the
; Temperature method with no explicit options (see CalibrationSweepResolver.ResolveTemperature).
;
; Each band below is exactly the two setpoint commands OrcaSlicer/PrintFarmer both issue at the
; start of that band, in band order (band 1 = hottest, descending to the last/coolest band).
M104 S240
M109 S240
M104 S235
M109 S235
M104 S230
M109 S230
M104 S225
M109 S225
M104 S220
M109 S220
M104 S215
M109 S215
M104 S210
M109 S210
M104 S205
M109 S205
M104 S200
M109 S200
