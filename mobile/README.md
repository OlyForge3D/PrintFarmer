# PrintFarmer

iOS app for managing 3D printer farms.

## About

PrintFarmer is a SwiftUI-based iOS application for monitoring and managing
multiple 3D printers across one or more registered PrintFarmer servers. Features
include printer status monitoring, filament/spool management, job queue viewing,
server switching, and real-time updates via SignalR.

## Tech Stack

- **Language:** Swift
- **UI Framework:** SwiftUI
- **Minimum Target:** iOS 17+
- **Concurrency:** Swift Concurrency (async/await)
- **Architecture:** MVVM with repository pattern
- **Backend:** [PrintFarmer API](https://github.com/OlyForge3D) (ASP.NET Core)

## Requirements

- Xcode 26+
- iOS 17+ deployment target

## Getting Started

1. Clone the repository:
   ```bash
   git clone https://github.com/OlyForge3D/PFarm-Ios.git
   cd PFarm-Ios
   ```

2. Open `PrintFarmer.xcodeproj` in Xcode.

3. Optional for development: set the `PRINTFARMER_API_URL` environment variable
   in your Xcode scheme to seed the initial server. For local PrintFarmer
   development, use `http://localhost:5245`.

4. Build and run on a simulator or device (iOS 17+).

## Server Configuration

The app supports multiple registered PrintFarmer backend servers. Server
registrations are stored locally in UserDefaults on the device, and each server
keeps its own Keychain-stored credentials.

### Managing Servers

- On first launch, register a PrintFarmer server before signing in.
- After setup, open **Settings** → **Manage Servers** to add, edit, or delete servers.
- The server editor normalizes URLs and rejects duplicates.
- Use **Check Connection** to verify reachability. The app checks `/health` and
  `/healthz`; network failures are shown in the editor and saved status appears
  in the server list.

### Switching Servers

- On iPhone, use the toolbar server switcher from the main app screens.
- On iPad, use the server switcher in the sidebar.
- Switching servers rebuilds the app's API, authentication, and SignalR services
  for the newly active server.

### Development URL Seeding

`PRINTFARMER_API_URL` is now a development seed/override for the server registry,
not the only server the app can use. Set it in the Xcode scheme when you want a
simulator or device run to start with a specific backend, such as the local .NET
API:

```bash
PRINTFARMER_API_URL=http://localhost:5245
```

Existing installs that saved a single legacy URL under `pf_server_url` migrate
that URL into the server registry on first launch and make it the initial active
server.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
