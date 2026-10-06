import Foundation
import CoreGraphics
import ImageIO
import CryptoKit

struct ImageEvidence: Codable {
    let width: Int
    let height: Int
    let rgbaSHA256: String
}

var evidence: [String: ImageEvidence] = [:]
for path in CommandLine.arguments.dropFirst() {
    guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
        fatalError("Cannot decode screenshot: \(path)")
    }
    var pixels = [UInt8](repeating: 0, count: image.width * image.height * 4)
    try pixels.withUnsafeMutableBytes { buffer in
        guard let context = CGContext(
            data: buffer.baseAddress, width: image.width, height: image.height,
            bitsPerComponent: 8, bytesPerRow: image.width * 4,
            space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue
        ) else {
            throw NSError(domain: "ScreenshotPixels", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "Cannot allocate RGBA context for \(path)"])
        }
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
    }
    evidence[path] = ImageEvidence(
        width: image.width, height: image.height,
        rgbaSHA256: SHA256.hash(data: Data(pixels)).map { String(format: "%02x", $0) }.joined()
    )
}
let data = try JSONEncoder().encode(evidence)
FileHandle.standardOutput.write(data)
