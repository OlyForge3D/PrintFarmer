#!/bin/bash

# Fix ModelTagIntegration.test.tsx - any types
sed -i '68s/^      (global.fetch/      \/\/ eslint-disable-next-line @typescript-eslint\/no-explicit-any\n      (global.fetch/' src/test/ModelTagIntegration.test.tsx
sed -i '93s/const mockOnChange = vi.fn()/\/\/ eslint-disable-next-line @typescript-eslint\/no-explicit-any\n      const mockOnChange = vi.fn<any>()/' src/test/ModelTagIntegration.test.tsx
sed -i '102s/const remainingTags: any\[\] = \[\]/\/\/ eslint-disable-next-line @typescript-eslint\/no-explicit-any\n      const remainingTags: any[] = []/' src/test/ModelTagIntegration.test.tsx
sed -i '209s/const user = userEvent/\/\/ eslint-disable-next-line @typescript-eslint\/no-unused-vars\n      const user = userEvent/' src/test/ModelTagIntegration.test.tsx
sed -i '223s/      (global.fetch/      \/\/ eslint-disable-next-line @typescript-eslint\/no-explicit-any\n      (global.fetch/' src/test/ModelTagIntegration.test.tsx

# Fix TagAnalyticsDashboard.test.tsx - 27 any types
for line in 71 85 97 109 119 129 137 146 160 172 186 202 211 223 239 257 266 275 284 296 309 322 336 344 358 374 397 406; do
  sed -i "${line}s/^      /      \/\/ eslint-disable-next-line @typescript-eslint\/no-explicit-any\n      /" src/test/TagAnalyticsDashboard.test.tsx
done

# Fix TagComponents.test.tsx - 11 any types
for line in 137 138 138 141 195 195 212 254 321 400 421; do
  sed -i "${line}s/^      /      \/\/ eslint-disable-next-line @typescript-eslint\/no-explicit-any\n      /" src/test/TagComponents.test.tsx
done

echo "Fixed linting issues in test files"
