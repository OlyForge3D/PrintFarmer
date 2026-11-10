import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { blankSourceComments, detectUiText } from '../source-adapter.mjs';
import { scanChangedUiFiles } from '../check-ui-design.mjs';

const file = 'src/Web/ReactApp/src/example.tsx';
const imageFindings = source => detectUiText(source, file).filter(finding => finding.antipattern === 'broken-image');
const borderFindings = source => detectUiText(source, file).filter(finding =>
  finding.antipattern === 'border-accent-on-rounded');

test('JSDoc/comment examples are not live images; real missing-src and empty-src JSX remain findings', () => {
  const source = '/** Get a React <img> element. */\n// <img>\nconst real = <img alt="Preview" />;';
  assert.deepEqual(imageFindings(source).map(finding => finding.line), [3]);
  assert.equal(imageFindings('<img src="" alt="Preview"/>').length, 1);
  assert.equal(imageFindings('<img src={entry.src} alt={entry.alt}/>').length, 0);
  assert.equal(imageFindings('<img\n src={entry.src}\n alt={entry.alt}\n/>').length, 0);
  assert.equal(imageFindings('<img src="/preview.png" />').length, 0);
  assert.equal(imageFindings('/** <img> */\n<img src="#" />')[0].line, 2);
});

test('JSX contractions do not hide later comments or live violations', () => {
  const source = '<p>Don\'t miss this</p>{/* <img> */}\n<img />\n' +
    'const text = "Don\'t strip /* this string */";';
  assert.deepEqual(imageFindings(source).map(finding => finding.line), [2]);
  assert.ok(blankSourceComments(source, file).includes('/* this string */'));
});

test('slash-dense input keeps bounded lexical context and retains trailing live findings', () => {
  const source = `${'value / value;\n'.repeat(20000)}// <img>\n<img />`;
  const masked = blankSourceComments(source, file);
  assert.equal(masked.length, source.length);
  assert.ok(masked.endsWith('        \n<img />'));
});

test('identifiers ending in return are division context, not the return keyword', () => {
  const source = 'const x = nonreturn / /* <img> */ value;\n<img />\nreturn /[/*]/;';
  assert.deepEqual(imageFindings(source).map(finding => finding.line), [2]);
  assert.ok(blankSourceComments(source, file).endsWith('return /[/*]/;'));
});

test('comment masking retains UTF-16 offsets, newlines and live strings/templates/styles', () => {
  const source = '/* emoji \u{1f600} <img>\r\nborder-l-4 */\r\nconst url="https://example.com";\n' +
    'const regex = /[/*]/;\nconst template = `<img>`;\nconst css = `border-left: 4px solid red`;';
  const masked = blankSourceComments(source, file);
  assert.equal(masked.length, source.length);
  assert.deepEqual([...masked.matchAll(/\r?\n/g)].map(match => match.index),
    [...source.matchAll(/\r?\n/g)].map(match => match.index));
  assert.equal(masked.split('\n')[2], 'const url="https://example.com";');
  assert.ok(masked.includes('/[/*]/'));
  assert.ok(masked.includes('`<img>`'));
  assert.equal(imageFindings(source)[0].line, 5);
  assert.ok(detectUiText(source, file).some(finding => finding.antipattern === 'side-tab'));
});

test('only empty, square, explicitly spinning rounded loading rings avoid card-accent findings', () => {
  assert.equal(borderFindings('<div className="pf-animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>').length, 0);
  assert.equal(borderFindings('<span class="animate-spin rounded-full h-4 w-4 border-b-2"></span>').length, 0);
  assert.equal(borderFindings('<div className="pf-animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent" />').length, 0);
  assert.equal(borderFindings('<span className="animate-spin rounded-full h-4 w-4 border-t-2" />').length, 0);
  for (const source of [
    '<div className="rounded-lg border-b-2">Card</div>',
    '<div className="rounded-full h-8 w-8 border-b-2"></div>',
    '<div className="animate-spin rounded-lg h-8 w-8 border-b-2"></div>',
    '<div className="animate-spin rounded-full h-8 w-12 border-b-2"></div>',
    '<div className="animate-spin rounded-full h-8 w-8 border-b-2">Card</div>',
    '<div className="animate-spin rounded-full h-8 w-8 border-b-2"></div><div className="rounded-lg border-b-2">Card</div>',
    '<div className="rounded-lg border-b-2" />',
    '<div className="rounded-full h-8 w-8 border-b-2" />',
    '<div className="animate-spin rounded-lg h-8 w-8 border-b-2" />',
    '<div className="animate-spin rounded-full h-8 w-12 border-b-2" />',
    '<div className={clsx("rounded-none border-b-2", selected && "border-pf-accent")} />',
  ]) assert.ok(borderFindings(source).length > 0, source);
  assert.ok(detectUiText('<div className="animate-spin rounded-full h-8 w-8 border-l-4" />', file)
    .some(finding => finding.antipattern === 'side-tab'));
});

test('only the source-pinned BlobImage callback avoids a missing-src finding at its own image', () => {
  const thumbnailFile = 'src/Web/ReactApp/src/features/files/components/FileThumbnail.tsx';
  const source = `function BlobImage({ blob, alt, className }: BlobImageProps) {
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
  const find = (text, target = thumbnailFile) => detectUiText(text, target, { inlineIgnores: false })
    .filter(finding => finding.antipattern === 'broken-image');
  assert.equal(find(source).length, 0);
  assert.equal(find(source.replaceAll('\n', '\r\n'), path.resolve(thumbnailFile)).length, 0);
  assert.deepEqual(find(`${source}\n<img />\n<img src="" />\n<img ref={other} />`).map(finding =>
    finding.line).sort((a, b) => a - b), [13, 14, 15]);
  for (const changed of [
    source.replace('image.src = url;', ''),
    source.replace('URL.createObjectURL(blob)', '""'),
    source.replace('URL.revokeObjectURL(url);', ''),
    source.replace("image.removeAttribute('src');", ''),
    source.replace('}, [blob]);', '}, []);'),
    source.replace('ref={attach}', 'ref={other}'),
    `${source}\n${source}`,
    `const example = \`${source}\`;`,
    `/* ${source} */\n<img ref={attach} />`,
  ]) assert.ok(find(changed).length > 0, changed);
  assert.equal(find(source, file).length, 1);
});

test('original comment-based suppressions still apply after masking and raw mode does not suppress', () => {
  const source = '// impeccable-disable-next-line side-tab -- intentional warning\n' +
    '<div className="rounded-lg border-l-4">Warning</div>';
  assert.equal(detectUiText(source, file).length, 0);
  assert.equal(detectUiText(source, file, { inlineIgnores: false })[0].line, 2);
  assert.equal(detectUiText('/* impeccable-disable border-accent-on-rounded -- approved */\n' +
    '<div className="rounded-lg border-b-2">Card</div>', file).length, 0);
});

test('changed-file scanner uses the adapter fixes without losing real violations', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'printfarmer-adapter-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
  fs.writeFileSync(path.join(root, file), '/** React <img> */\n' +
    '<div className="pf-animate-spin rounded-full h-8 w-8 border-b-2" />\n' +
    '<img />\n<div className="rounded-lg border-b-2">Card</div>');
  const result = await scanChangedUiFiles(root, [{ status: 'M', paths: [file], currentPath: file }]);
  assert.deepEqual(result.findings.map(finding => [finding.antipattern, finding.line]), [
    ['border-accent-on-rounded', 4], ['broken-image', 3],
  ]);
});
