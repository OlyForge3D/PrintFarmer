## OctoPrint vs. Moonraker API Parity

This document summarizes the differences in API field support between OctoPrint and Moonraker backends in PrintFarmer, and lists recommended OctoPrint plugins for closer parity.

### Core fields (supported for both)
- Online status
- State
- Progress
- Job name
- Hotend temp/target
- Bed temp/target
- Camera stream URL
- Manufacturer/model
- API key, IP, original server URL

### Moonraker-only (or richer by default)
- X, Y, Z position (head position)
- Camera snapshot URL
- Thumbnail URL
- Spool/filament info
- More granular state/error info
- Print time, estimated time left, job metadata
- Homed axes

### OctoPrint: What can be added with plugins

#### X/Y/Z Position
- [Display Current Position](https://plugins.octoprint.org/plugins/display_current_position/) or [Position Info](https://plugins.octoprint.org/plugins/positioninfo/)

#### Spool/Filament Info
- [SpoolManager](https://plugins.octoprint.org/plugins/SpoolManager/)

#### Camera Snapshot URL
- [MultiCam](https://plugins.octoprint.org/plugins/multicam/)

#### Thumbnail URL
- [PrintJobHistory](https://plugins.octoprint.org/plugins/PrintJobHistory/)

#### More granular state/error info
- [Print Status](https://plugins.octoprint.org/plugins/printstatus/)

### Not possible without custom plugins/macros
- Homed axes
- Some job metadata
- Some error states

**For closest parity, install these OctoPrint plugins:**
- Display Current Position or Position Info (for X/Y/Z)
- SpoolManager (for filament/spool info)
- MultiCam (for snapshot URLs)
- PrintJobHistory (for thumbnails)
- Print Status (for richer state info)