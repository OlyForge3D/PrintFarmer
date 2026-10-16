import { describe, expect, it } from 'vitest';
import { getMjpegBoundary, MjpegParser } from '@/features/cameras/utils/mjpegParser';

const boundary = 'camera-frame';
const jpeg = new Uint8Array([0xff, 0xd8, 1, 2, 3, 0xff, 0xd9]);
const encoder = new TextEncoder();

function multipartFrame(includeLength: boolean): Uint8Array {
  const header = `--${boundary}\r\nContent-Type: image/jpeg\r\n${includeLength ? `Content-Length: ${jpeg.length}\r\n` : ''}\r\n`;
  const ending = `\r\n--${boundary}--\r\n`;
  const bytes = new Uint8Array(encoder.encode(header).length + jpeg.length + encoder.encode(ending).length);
  let offset = 0;
  const headerBytes = encoder.encode(header);
  const endingBytes = encoder.encode(ending);
  bytes.set(headerBytes, offset); offset += headerBytes.length;
  bytes.set(jpeg, offset); offset += jpeg.length;
  bytes.set(endingBytes, offset);
  return bytes;
}

describe('MjpegParser', () => {
  it('parses a complete frame from a single chunk', () => {
    const parser = new MjpegParser(boundary);
    expect(parser.push(multipartFrame(true))).toEqual([jpeg]);
  });

  it('parses frames when headers, boundaries, and JPEG bytes split across chunks', () => {
    const parser = new MjpegParser(boundary);
    const bytes = multipartFrame(true);
    const frames = [
      ...parser.push(bytes.slice(0, 5)),
      ...parser.push(bytes.slice(5, 22)),
      ...parser.push(bytes.slice(22, 27)),
      ...parser.push(bytes.slice(27)),
    ];
    expect(frames).toEqual([jpeg]);
  });

  it('parses frames without Content-Length by locating JPEG end/boundary markers', () => {
    const parser = new MjpegParser(boundary);
    const bytes = multipartFrame(false);
    const frames = [
      ...parser.push(bytes.slice(0, -14)),
      ...parser.push(bytes.slice(-14)),
    ];
    expect(frames).toEqual([jpeg]);
  });

  it('resets an oversized incomplete buffer and resumes parsing subsequent frames', () => {
    const parser = new MjpegParser(boundary, 256);
    expect(parser.push(new Uint8Array(257))).toEqual([]);
    expect(parser.didResetAfterOverflow()).toBe(true);
    expect(parser.push(multipartFrame(true))).toEqual([jpeg]);
  });

  it('extracts quoted MIME boundaries', () => {
    expect(getMjpegBoundary('multipart/x-mixed-replace; boundary="camera-frame"')).toBe(boundary);
    expect(getMjpegBoundary('image/jpeg')).toBeNull();
  });
});
