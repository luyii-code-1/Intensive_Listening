import assert from 'node:assert/strict';
import { readFile, readdir, stat } from 'node:fs/promises';

const output = new URL('../dist/', import.meta.url);
const pages = ['index', 'getting-started', 'installation', 'first-run', 'web-player', 'listening', 'settings', 'transcription', 'authoring', 'agents', 'faq'];
for (const page of pages) {
  const html = await readFile(new URL('docs/' + page + '.html', output), 'utf8');
  assert.ok(html.includes('/docs/assets/'), `${page}: documentation assets must use the /docs/ base`);
  assert.ok(html.includes('VPContent'), `${page}: missing rendered documentation content`);
}
const assets = await readdir(new URL('docs/assets/', output), { recursive: true });
assert.ok(assets.some(file => file.includes('localSearchIndex')), 'Missing local search index');
assert.ok((await stat(new URL('guide-redirect.js', output))).isFile(), 'Missing application tutorial redirect');
const home = await readFile(new URL('index.html', output), 'utf8');
assert.ok(home.includes('href="/docs/"'), 'Website must link to the documentation centre');
console.log(`Documentation output verified: ${pages.length} pages, /docs/ assets, local search and legacy redirects.`);
