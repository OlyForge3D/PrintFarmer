import path from 'node:path';
import { detectText } from './vendor/impeccable/scripts/detector/engines/regex/detect-text.mjs';
import { applyInlineIgnores } from './vendor/impeccable/scripts/detector/shared/inline-ignores.mjs';

const scriptExtensions = new Set(['.js', '.jsx', '.ts', '.tsx']);

export function blankSourceComments(source, file, onCode) {
  const script = scriptExtensions.has(path.extname(file).toLowerCase());
  const chars = source.split('');
  let quote;
  let comment;
  let precedingToken = '';
  let wordLength = 0;
  for (let index = 0; index < chars.length; index += 1) {
    const char = chars[index];
    const next = chars[index + 1];
    if (comment) {
      const end = comment === 'line' ? char === '\n'
        : comment === 'html' ? source.slice(index, index + 3) === '-->'
          : char === '*' && next === '/';
      if (end) {
        const length = comment === 'line' ? 0 : comment === 'html' ? 3 : 2;
        for (let offset = 0; offset < length; offset += 1) chars[index + offset] = ' ';
        index += Math.max(0, length - 1);
        comment = undefined;
      } else if (char !== '\n' && char !== '\r') chars[index] = ' ';
      continue;
    }
    if (quote) {
      if (char === '\\') index += 1;
      else if (char === quote) quote = undefined;
      continue;
    }
    onCode?.(index);
    if (char === "'" && /\w/.test(chars[index - 1] || '') && /\w/.test(next || '')) {
      precedingToken = '';
      wordLength = 0;
      continue;
    }
    if (char === '"' || char === "'" || char === '`') {
      quote = char;
      precedingToken = 'value';
      wordLength = 0;
      continue;
    }
    if (script && char === '/' && next !== '/' && next !== '*' &&
      (!precedingToken || /^[=(:,[!&|?;{}]$/.test(precedingToken) ||
        precedingToken === 'return' && wordLength === 6)) {
      let inClass = false;
      let end = index + 1;
      for (; end < chars.length && chars[end] !== '\n'; end += 1) {
        if (chars[end] === '\\') end += 1;
        else if (chars[end] === '[') inClass = true;
        else if (chars[end] === ']') inClass = false;
        else if (chars[end] === '/' && !inClass) break;
      }
      if (chars[end] === '/') {
        index = end;
        precedingToken = 'value';
        wordLength = 0;
      }
      continue;
    }
    if (char === '/' && next === '*') {
      comment = 'block';
      chars[index] = chars[index + 1] = ' ';
      index += 1;
    } else if (script && char === '/' && next === '/') {
      comment = 'line';
      chars[index] = chars[index + 1] = ' ';
      index += 1;
    } else if (!script && source.slice(index, index + 4) === '<!--') {
      comment = 'html';
      for (let offset = 0; offset < 4; offset += 1) chars[index + offset] = ' ';
      index += 3;
    } else if (/\w/.test(char)) {
      wordLength = /\w$/.test(precedingToken) ? Math.min(wordLength + 1, 7) : 1;
      precedingToken = /\w$/.test(precedingToken) ? `${precedingToken}${char}`.slice(-6) : char;
    } else if (!/\s/.test(char)) {
      precedingToken = char;
      wordLength = 0;
    }
  }
  return chars.join('');
}

function blankLoadingRingBorders(content) {
  return content.replace(/<(div|span)\b[^>\n]*\bclass(?:Name)?\s*=\s*["']([^"'\n]*)["'][^>\n]*(?:\/>|>[ \t]*<\/\1\s*>)/g,
    (element, tag, classes) => {
      const tokens = new Set(classes.split(/\s+/));
      const height = [...tokens].find(token => /^h-\d+$/.test(token));
      if (!tokens.has('rounded-full') || !(tokens.has('animate-spin') || tokens.has('pf-animate-spin')) ||
        !height || !tokens.has(`w-${height.slice(2)}`)) return element;
      const masked = classes.replace(/\bborder-[tb]-\d+\b/g, token => ' '.repeat(token.length));
      return element.replace(classes, masked);
    });
}

// Per-site source proof, not a general exemption for ref-driven images.
const blobImageFile = 'src/Web/ReactApp/src/features/files/components/FileThumbnail.tsx';
const blobImageSource = `function BlobImage({ blob, alt, className }: BlobImageProps) {
  const attach = useCallback((image: HTMLImageElement | null) => {
    if (!image || typeof URL.createObjectURL !== 'function') return undefined;
    const url = URL.createObjectURL(blob);
    image.src = url;
    return () => {
      image.removeAttribute('src');
      URL.revokeObjectURL(url);
    };
  }, [blob]);
  return <img ref={attach} alt={alt} className={className} />;
}`;

function sourceProvenBlobImageLine(source, file) {
  const normalizedFile = file.replaceAll('\\', '/');
  if (normalizedFile !== blobImageFile && !normalizedFile.endsWith(`/${blobImageFile}`)) return undefined;
  const normalizedSource = source.replaceAll('\r\n', '\n');
  const start = normalizedSource.indexOf(blobImageSource);
  if (start === -1 || normalizedSource.lastIndexOf(blobImageSource) !== start) return undefined;
  let startsInCode = false;
  blankSourceComments(normalizedSource, file, index => {
    if (index === start) startsInCode = true;
  });
  if (!startsInCode) return undefined;
  const imageOffset = start + blobImageSource.indexOf('<img');
  return normalizedSource.slice(0, imageOffset).split('\n').length;
}

export function detectUiText(source, file, options = {}) {
  const content = blankLoadingRingBorders(blankSourceComments(source, file));
  const blobImageLine = sourceProvenBlobImageLine(content, file);
  const findings = detectText(content, file, { ...options, inlineIgnores: false }).filter(finding =>
    finding.antipattern !== 'broken-image' || finding.line !== blobImageLine);
  return options.inlineIgnores === false ? findings : applyInlineIgnores(findings, source);
}
