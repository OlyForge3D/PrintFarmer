const interactiveControl = /\b(?:Button|NavigationLink)\s*\(/g;
const fixedFrame = /\.frame\s*\(\s*(?:width:\s*(\d+(?:\.\d+)?)\s*,\s*height:\s*(\d+(?:\.\d+)?)|height:\s*(\d+(?:\.\d+)?)\s*,\s*width:\s*(\d+(?:\.\d+)?))\s*\)\s*;?\s*$/;

function stripSwiftComments(source) {
  return String(source)
    .replace(/"(?:\\.|[^"\\])*"/g, '""')
    .replace(/\/\*[\s\S]*?\*\//g, match => match.replace(/[^\r\n]/g, ' '))
    .split(/\r?\n/)
    .map(line => line.replace(/\/\/.*$/, ''))
    .join('\n');
}

export function detectSmallSwiftUiTargets(source, filePath) {
  const findings = [];
  const lines = stripSwiftComments(source).split(/\r?\n/);

  for (const [index, line] of lines.entries()) {
    interactiveControl.lastIndex = 0;
    let control;
    while ((control = interactiveControl.exec(line)) !== null) {
      const suffix = line.slice(control.index);
      const frame = suffix.match(fixedFrame);
      if (!frame) continue;

      const width = Number(frame[1] ?? frame[4]);
      const height = Number(frame[2] ?? frame[3]);
      if (width >= 44 && height >= 44) continue;

      findings.push({
        antipattern: 'swiftui-small-interactive-target',
        description: 'Fixed SwiftUI Button or NavigationLink frame is below the 44 pt minimum on at least one axis.',
        file: filePath,
        line: index + 1,
        snippet: line.trim(),
        value: `${width}x${height}`,
      });
      break;
    }
  }

  return findings;
}

export { stripSwiftComments };
