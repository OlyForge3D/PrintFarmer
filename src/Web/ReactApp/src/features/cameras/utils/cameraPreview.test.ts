import { describe, expect, it } from "vitest";
import {
  CameraAccessMode,
  CameraSnapshotStrategy,
  CameraStreamFormat,
} from "@/types/api";
import {
  canUseMjpegStream,
  isUnsupportedCameraPreview,
  shouldPollPrinterSnapshot,
} from "@/features/cameras/utils/cameraPreview";

describe("cameraPreview", () => {
  it("polls authenticated same-origin printer camera proxies", () => {
    const contract = {
      accessMode: CameraAccessMode.Unknown,
      streamFormat: CameraStreamFormat.Unknown,
      snapshotStrategy: CameraSnapshotStrategy.None,
      snapshotUrl: "/api/printers/printer-1/camera/snapshot",
    };

    expect(shouldPollPrinterSnapshot(contract)).toBe(true);
    expect(isUnsupportedCameraPreview(contract)).toBe(false);
    expect(canUseMjpegStream(contract)).toBe(false);
  });

  it("keeps a configured MJPEG stream available when its snapshot also uses the authenticated proxy", () => {
    expect(canUseMjpegStream({
      accessMode: CameraAccessMode.StreamAndSnapshot,
      streamFormat: CameraStreamFormat.Mjpeg,
      streamUrl: "/api/cameras/camera-1/stream",
      snapshotStrategy: CameraSnapshotStrategy.DirectUrl,
      snapshotUrl: "/api/cameras/camera-1/snapshot",
    })).toBe(true);
  });

  it("does not expose unpreviewable RTSP or WebRTC streams as selectable MJPEG modes", () => {
    for (const streamFormat of [CameraStreamFormat.Rtsp, CameraStreamFormat.WebRtc]) {
      expect(canUseMjpegStream({
        accessMode: CameraAccessMode.StreamAndSnapshot,
        streamFormat,
        streamUrl: "/api/cameras/camera-1/stream",
        snapshotStrategy: CameraSnapshotStrategy.DirectUrl,
        snapshotUrl: "/api/cameras/camera-1/snapshot",
      })).toBe(false);
    }
  });

  it("polls snapshot paths with query strings but not stream paths", () => {
    expect(shouldPollPrinterSnapshot({
      snapshotStrategy: CameraSnapshotStrategy.None,
      snapshotUrl: "/api/printers/printer-1/camera/snapshot?quality=high",
    })).toBe(true);
    expect(shouldPollPrinterSnapshot({
      snapshotStrategy: CameraSnapshotStrategy.None,
      snapshotUrl: "/api/printers/printer-1/camera/stream?path=/snapshot",
    })).toBe(false);
  });

  it("leaves public direct snapshot URLs in direct-browser mode", () => {
    const contract = {
      accessMode: CameraAccessMode.SnapshotOnly,
      streamFormat: CameraStreamFormat.Mjpeg,
      snapshotStrategy: CameraSnapshotStrategy.DirectUrl,
      snapshotUrl: "http://camera.local/snapshot.jpg",
    };

    expect(shouldPollPrinterSnapshot(contract)).toBe(false);
    expect(isUnsupportedCameraPreview(contract)).toBe(false);
  });
});
