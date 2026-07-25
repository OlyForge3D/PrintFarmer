# PrintFarmer

iOS app for managing 3D printer farms.

## About

PrintFarmer is a SwiftUI-based iOS application for monitoring and managing multiple 3D printers. Features include printer status monitoring, filament/spool management, job queue viewing, and real-time updates via SignalR.

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

3. Set the API server URL via the `PRINTFARMER_API_URL` environment variable, or update `PrintFarmer/Utilities/AppConfig.swift` to point to your backend instance.

4. Build and run on a simulator or device (iOS 17+).

## Configuration

The app connects to a PrintFarmer backend API. By default it targets `http://localhost:5000`. Override this by setting the `PRINTFARMER_API_URL` environment variable in your Xcode scheme.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
