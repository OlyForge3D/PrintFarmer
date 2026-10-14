const MAX_MJPEG_BUFFER_BYTES = 8 * 1024 * 1024;
const HEADER_SEPARATOR = new Uint8Array([13, 10, 13, 10]);
const JPEG_START = new Uint8Array([0xff, 0xd8]);
const JPEG_END = new Uint8Array([0xff, 0xd9]);

function findBytes(haystack: Uint8Array, needle: Uint8Array, start = 0): number {
  outer: for (let i = start; i <= haystack.length - needle.length; i++) {
    for (let j = 0; j < needle.length; j++) if (haystack[i + j] !== needle[j]) continue outer;
    return i;
  }
  return -1;
}

function concat(left: Uint8Array, right: Uint8Array): Uint8Array {
  const result = new Uint8Array(left.length + right.length);
  result.set(left);
  result.set(right, left.length);
  return result;
}

export function getMjpegBoundary(contentType: string | null): string | null {
  if (!contentType || !contentType.toLowerCase().includes('multipart/x-mixed-replace')) return null;
  const parameter = contentType.match(/(?:^|;)\s*boundary\s*=\s*(?:"([^"]+)"|([^;\s]+))/i);
  const boundary = parameter?.[1] ?? parameter?.[2];
  return boundary ? boundary.replace(/^--/, '') : null;
}

export class MjpegParser {
  private buffer: Uint8Array<ArrayBufferLike> = new Uint8Array();
  private readonly delimiter: Uint8Array<ArrayBufferLike>;
  private discarded = false;

  constructor(boundary: string, private readonly maxBufferBytes = MAX_MJPEG_BUFFER_BYTES) {
    this.delimiter = new TextEncoder().encode(`--${boundary.replace(/^--/, '')}`);
  }

  push(chunk: Uint8Array): Uint8Array[] {
    this.buffer = concat(this.buffer, chunk);
    if (this.buffer.length > this.maxBufferBytes) {
      this.buffer = new Uint8Array();
      this.discarded = true;
      return [];
    }

    const frames: Uint8Array[] = [];
    while (true) {
      const boundaryAt = findBytes(this.buffer, this.delimiter);
      if (boundaryAt < 0) {
        const keep = Math.min(this.buffer.length, this.delimiter.length - 1);
        this.buffer = this.buffer.slice(this.buffer.length - keep);
        break;
      }
      const afterBoundary = boundaryAt + this.delimiter.length;
      if (this.buffer.length < afterBoundary + 2) {
        this.buffer = this.buffer.slice(boundaryAt);
        break;
      }
      if (this.buffer[afterBoundary] === 45 && this.buffer[afterBoundary + 1] === 45) {
        this.buffer = new Uint8Array();
        break;
      }
      const headerStart = afterBoundary + (this.buffer[afterBoundary] === 13 && this.buffer[afterBoundary + 1] === 10 ? 2 : 0);
      const headerEnd = findBytes(this.buffer, HEADER_SEPARATOR, headerStart);
      if (headerEnd < 0) {
        this.buffer = this.buffer.slice(boundaryAt);
        break;
      }
      const headers = new TextDecoder().decode(this.buffer.slice(headerStart, headerEnd));
      const lengthHeader = headers.match(/(?:^|\r\n)content-length\s*:\s*(\d+)/i);
      const contentStart = headerEnd + HEADER_SEPARATOR.length;
      if (lengthHeader) {
        const length = Number(lengthHeader[1]);
        if (!Number.isSafeInteger(length) || length < 0 || length > this.maxBufferBytes) {
          this.buffer = new Uint8Array();
          break;
        }
        if (this.buffer.length < contentStart + length) {
          this.buffer = this.buffer.slice(boundaryAt);
          break;
        }
        const jpeg = this.buffer.slice(contentStart, contentStart + length);
        if (findBytes(jpeg, JPEG_START) >= 0 && findBytes(jpeg, JPEG_END) >= 0) frames.push(jpeg);
        this.buffer = this.buffer.slice(contentStart + length);
        continue;
      }

      const nextBoundary = findBytes(this.buffer, this.delimiter, contentStart);
      if (nextBoundary < 0) {
        const start = findBytes(this.buffer, JPEG_START, contentStart);
        const end = start >= 0 ? findBytes(this.buffer, JPEG_END, start + 2) : -1;
        if (start >= 0 && end >= 0) {
          frames.push(this.buffer.slice(start, end + 2));
          this.buffer = this.buffer.slice(end + 2);
        } else {
          this.buffer = this.buffer.slice(boundaryAt);
        }
        break;
      }
      const jpeg = this.buffer.slice(contentStart, nextBoundary);
      const start = findBytes(jpeg, JPEG_START);
      const end = start >= 0 ? findBytes(jpeg, JPEG_END, start + 2) : -1;
      if (start >= 0 && end >= 0) frames.push(jpeg.slice(start, end + 2));
      this.buffer = this.buffer.slice(nextBoundary);
    }
    return frames;
  }

  didResetAfterOverflow(): boolean {
    const wasDiscarded = this.discarded;
    this.discarded = false;
    return wasDiscarded;
  }
}
