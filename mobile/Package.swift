// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "PrintFarmer",
    platforms: [
        .iOS(.v17),
        .macOS(.v14)
    ],
    products: [
        .library(
            name: "PrintFarmer",
            targets: ["PrintFarmer"]
        )
    ],
    dependencies: [
        .package(url: "https://github.com/evgenyneu/keychain-swift.git", from: "24.0.0"),
    ],
    targets: [
        .target(
            name: "PrintFarmer",
            dependencies: [
                .product(name: "KeychainSwift", package: "keychain-swift"),
            ],
            path: "PrintFarmer"
        ),
        .testTarget(
            name: "PrintFarmerTests",
            dependencies: ["PrintFarmer"],
            path: "PrintFarmerTests"
        )
    ]
)
