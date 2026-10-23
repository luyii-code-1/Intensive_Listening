import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, stat } from 'node:fs/promises';
import vm from 'node:vm';

const source = await readFile(new URL('../guide-redirect.js', import.meta.url), 'utf8');
const routes = {
  quickstart: 'getting-started', 'web-player': 'web-player', prepare: 'installation',
  'first-run': 'first-run', 'api-setup': 'transcription', teacher: 'authoring',
  agent: 'agents', student: 'listening', settings: 'settings', help: 'faq'
};

test('existing application tutorial links reach their corresponding Markdown pages', async () => {
  for (const [section, page] of Object.entries(routes)) {
    let target;
    vm.runInNewContext(source, { window: { location: { hash: '#' + section, replace(url) { target = url; } } } });
    assert.equal(target, '/docs/' + page + '.html');
    assert.ok((await stat(new URL('../docs/' + page + '.md', import.meta.url))).isFile());
  }
});

test('unrecognised tutorial fragments reach the documentation home', () => {
  for (const hash of ['', '#unknown', '#__proto__', '#https://example.invalid/']) {
    let target;
    vm.runInNewContext(source, { window: { location: { hash, replace(url) { target = url; } } } });
    assert.equal(target, '/docs/');
  }
});
