import { cp, mkdir, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join, relative } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const output = join(root, 'dist');
if (dirname(output) !== root.replace(/[\\/]$/, '') || relative(root, output) !== 'dist') {
  throw new Error('Invalid site output directory');
}
await rm(output, { recursive: true, force: true });
await mkdir(output);
for (const file of ['index.html', 'guide.html', 'styles.css', 'release.js', 'assets']) {
  await cp(join(root, file), join(output, file), { recursive: true });
}
console.log('Static site built in dist/; EdgeOne routes are in root edge-functions/.');
