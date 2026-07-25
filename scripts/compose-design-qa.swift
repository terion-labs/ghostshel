#!/usr/bin/env swift

import AppKit
import Foundation

guard CommandLine.arguments.count == 4 else {
    FileHandle.standardError.write(
        Data("usage: compose-design-qa.swift <reference.png> <render.png> <output.png>\n".utf8)
    )
    exit(64)
}

let referencePath = CommandLine.arguments[1]
let renderPath = CommandLine.arguments[2]
let outputPath = CommandLine.arguments[3]

func loadImage(at path: String) throws -> NSBitmapImageRep {
    guard
        let data = FileManager.default.contents(atPath: path),
        let image = NSBitmapImageRep(data: data)
    else {
        throw CocoaError(.fileReadCorruptFile, userInfo: [NSFilePathErrorKey: path])
    }

    return image
}

do {
    let reference = try loadImage(at: referencePath)
    let render = try loadImage(at: renderPath)
    guard
        reference.pixelsWide == render.pixelsWide,
        reference.pixelsHigh == render.pixelsHigh
    else {
        throw CocoaError(
            .coderInvalidValue,
            userInfo: [
                NSLocalizedDescriptionKey:
                    "Reference and render must use the same viewport. "
                    + "Reference is \(reference.pixelsWide)x\(reference.pixelsHigh); "
                    + "render is \(render.pixelsWide)x\(render.pixelsHigh).",
            ]
        )
    }

    let labelHeight = 72
    let canvasWidth = reference.pixelsWide + render.pixelsWide
    let canvasHeight = reference.pixelsHigh + labelHeight
    guard let canvas = NSBitmapImageRep(
        bitmapDataPlanes: nil,
        pixelsWide: canvasWidth,
        pixelsHigh: canvasHeight,
        bitsPerSample: 8,
        samplesPerPixel: 4,
        hasAlpha: true,
        isPlanar: false,
        colorSpaceName: .deviceRGB,
        bytesPerRow: 0,
        bitsPerPixel: 0
    ) else {
        throw CocoaError(.coderInvalidValue)
    }

    NSGraphicsContext.saveGraphicsState()
    defer { NSGraphicsContext.restoreGraphicsState() }
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: canvas)

    NSColor(calibratedWhite: 0.06, alpha: 1).setFill()
    NSRect(x: 0, y: 0, width: canvasWidth, height: canvasHeight).fill()

    let imageSize = NSSize(width: reference.pixelsWide, height: reference.pixelsHigh)
    NSImage(cgImage: reference.cgImage!, size: imageSize).draw(
        in: NSRect(x: 0, y: 0, width: imageSize.width, height: imageSize.height)
    )
    NSImage(cgImage: render.cgImage!, size: imageSize).draw(
        in: NSRect(
            x: CGFloat(reference.pixelsWide),
            y: 0,
            width: imageSize.width,
            height: imageSize.height
        )
    )

    let labelAttributes: [NSAttributedString.Key: Any] = [
        .font: NSFont.systemFont(ofSize: 30, weight: .semibold),
        .foregroundColor: NSColor.white,
    ]
    ("PENCIL REFERENCE" as NSString).draw(
        at: NSPoint(x: 24, y: CGFloat(reference.pixelsHigh + 18)),
        withAttributes: labelAttributes
    )
    ("RUNNING BUILD" as NSString).draw(
        at: NSPoint(
            x: CGFloat(reference.pixelsWide + 24),
            y: CGFloat(reference.pixelsHigh + 18)
        ),
        withAttributes: labelAttributes
    )

    guard let png = canvas.representation(using: .png, properties: [:]) else {
        throw CocoaError(.coderInvalidValue)
    }
    try png.write(to: URL(fileURLWithPath: outputPath), options: .atomic)
} catch {
    FileHandle.standardError.write(Data("compose-design-qa: \(error)\n".utf8))
    exit(1)
}
