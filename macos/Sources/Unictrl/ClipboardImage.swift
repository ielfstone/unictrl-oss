import AppKit
import ImageIO
import UniformTypeIdentifiers

/// Decode off the main queue; inspect dimensions before allocating a bitmap.
enum ClipboardImage {
    static func convert(_ data: Data) -> (png: Data, tiff: Data)? {
        guard data.count <= 100_000_000,
              let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              CGImageSourceGetCount(source) == 1,
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              ClipboardReplica.validSize(width, height),
              let image = CGImageSourceCreateImageAtIndex(source, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary) else { return nil }
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, UTType.png.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination), output.length <= ClipboardReplica.maxImageBytes,
              let tiff = NSBitmapImageRep(cgImage: image).representation(using: .tiff, properties: [:]) else { return nil }
        return (output as Data, tiff)
    }
}
