import assert from 'node:assert/strict'
import { test } from 'node:test'
import { readFile } from 'node:fs/promises'
import vm from 'node:vm'

const appSource = await readFile(new URL('../js/app.js', import.meta.url), 'utf8')
const storageSource = await readFile(new URL('../js/storage.js', import.meta.url), 'utf8')
const workerSource = await readFile(new URL('../sw.js', import.meta.url), 'utf8')
const cacheName = /const CACHE_NAME = '([^']+)'/.exec(workerSource)[1]

function appFixture() {
  const saved = [], timers = new Map()
  let next = 0
  const context = vm.createContext({
    document: { addEventListener() {} }, window: {}, console,
    localStorage: { getItem() { throw new Error('denied') }, setItem() { throw new Error('denied') } },
    lessonStorage: { async updateProgress(id, value) { saved.push({ id, position: value.position }) } },
    setTimeout(callback) { timers.set(++next, callback); return next }, clearTimeout(id) { timers.delete(id) }
  })
  vm.runInContext(appSource.replace(/^import .*;\r?\n/gm, '') + '\nglobalThis.App = IntensiveListeningApp;', context)
  const app = Object.create(context.App.prototype)
  Object.assign(app, { currentLesson: { id: 'first' }, currentTime: 1, revealedCloze: new Set(), showAlert() {} })
  return { app, saved, timers, context }
}

test('continuous playback keeps one save deadline and writes the latest snapshot', async () => {
  const { app, saved, timers } = appFixture()
  app.saveProgressDebounced()
  const deadline = app._progressTimer
  app.currentTime = 9; app.saveProgressDebounced()
  assert.equal(app._progressTimer, deadline)
  assert.equal(timers.size, 1)
  await app.flushProgress()
  assert.deepEqual(saved, [{ id: 'first', position: 9 }])
})

test('pending progress belongs to the original lesson after navigation', async () => {
  const { app, saved } = appFixture()
  app.currentTime = 12; app.saveProgressDebounced()
  app.currentLesson = null; app.currentTime = 0
  await app.flushProgress()
  assert.deepEqual(saved, [{ id: 'first', position: 12 }])
})

test('unavailable local storage leaves app settings usable', () => {
  const { app } = appFixture()
  assert.equal(app.readLocal('theme_mode'), null)
  assert.equal(app.writeLocal('theme_mode', 'dark'), false)
})

function storageFixture() {
  let transaction
  const db = { transaction() {
    const request = {}
    transaction = { request, objectStore: () => ({ put: () => request, delete: () => request }) }
    return transaction
  } }
  const context = vm.createContext({ indexedDB: { open() {
    const request = { result: db }
    queueMicrotask(() => request.onsuccess())
    return request
  } }, console })
  vm.runInContext(storageSource.replace('export class', 'class').replace('export const lessonStorage', 'const lessonStorage') + '\nglobalThis.Storage = LessonStorage;', context)
  return { storage: new context.Storage(), transaction: () => transaction }
}

test('lesson save waits for transaction commit and rejects a late abort', async () => {
  const { storage, transaction } = storageFixture()
  let complete = false
  const pending = storage.saveLesson({ id: 'lesson', cues: [] }).then(value => { complete = true; return value })
  await new Promise(resolve => setImmediate(resolve))
  const tx = transaction()
  tx.request.onsuccess?.()
  assert.equal(complete, false)
  tx.error = new Error('quota exceeded')
  const rejected = assert.rejects(pending, /quota exceeded/)
  tx.onabort()
  await rejected
})

test('lesson deletion resolves only after transaction commit', async () => {
  const { storage, transaction } = storageFixture()
  let deleted = false
  const pending = storage.deleteLesson('lesson').then(value => { deleted = value })
  await new Promise(resolve => setImmediate(resolve))
  const tx = transaction()
  tx.request.onsuccess?.()
  assert.equal(deleted, false)
  tx.oncomplete(); await pending
  assert.equal(deleted, true)
})

function workerFixture(options = {}) {
  const handlers = {}, deleted = []
  const cache = {
    async addAll() { if (options.installError) throw options.installError },
    async match() { return options.cachedResponse },
    async put() { if (options.cacheError) throw options.cacheError }
  }
  const context = vm.createContext({
    self: { location: { origin: 'https://player.test' }, addEventListener(name, handler) { handlers[name] = handler }, clients: { async claim() {} }, async skipWaiting() {} },
    caches: { async open() { if (options.openError) throw options.openError; return cache }, async keys() { return ['il-pwa-old', cacheName, 'other-project-assets'] }, async delete(name) { deleted.push(name) } },
    fetch: options.fetch || (async () => new Response('online', { status: 200 })), URL, Response,
    console: { warn() {} }
  })
  vm.runInContext(workerSource, context)
  return { handlers, deleted }
}

test('worker activation preserves caches belonging to other applications', async () => {
  const { handlers, deleted } = workerFixture()
  await new Promise((resolve, reject) => handlers.activate({ waitUntil(promise) { promise.then(resolve, reject) } }))
  assert.deepEqual(deleted, ['il-pwa-old'])
})

test('failed precaching prevents an incomplete worker from installing', async () => {
  const { handlers } = workerFixture({ installError: new Error('offline') })
  let pending
  handlers.install({ waitUntil(promise) { pending = promise } })
  await assert.rejects(pending, /offline/)
})

test('worker does not intercept third-party requests', () => {
  const { handlers } = workerFixture()
  let intercepted = false
  handlers.fetch({ request: { url: 'https://another.test/file.js', method: 'GET' }, respondWith() { intercepted = true } })
  assert.equal(intercepted, false)
})

test('cache write failure preserves the successful network response', async () => {
  const { handlers } = workerFixture({ cacheError: new Error('quota exceeded') })
  const background = []
  let result
  handlers.fetch({ request: { url: 'https://player.test/js/app.js', method: 'GET', mode: 'same-origin', destination: 'script' }, respondWith(promise) { result = promise }, waitUntil(promise) { background.push(promise) } })
  assert.equal(await (await result).text(), 'online')
  await Promise.all(background)
})

test('offline navigation without a cached shell returns a valid response', async () => {
  const { handlers } = workerFixture({ fetch: async () => { throw new Error('offline') } })
  let result
  handlers.fetch({ request: { url: 'https://player.test/', method: 'GET', mode: 'navigate' }, respondWith(promise) { result = promise }, waitUntil() {} })
  assert.equal((await result).status, 503)
})

test('unavailable cache storage still allows online resources', async () => {
  const { handlers } = workerFixture({ openError: new Error('storage denied') })
  let result
  const background = []
  handlers.fetch({ request: { url: 'https://player.test/js/app.js', method: 'GET', mode: 'same-origin' }, respondWith(promise) { result = promise }, waitUntil(promise) { background.push(promise) } })
  assert.equal(await (await result).text(), 'online')
  await Promise.all(background)
})

test('analytics loads only after an explicit current-policy choice, even for returning users', () => {
  const { app, context } = appFixture()
  const frames = []
  context.URL = URL
  context.window.location = { href: 'https://player.test/?ilppath=TOKEN_MARK#SECRET_MARK' }
  const overlay = { style: {} }
  app.dom = { blockingLegalOverlay: overlay }
  app.readLocal = () => null // Older consent is intentionally not the current choice.
  context.document.createElement = () => ({ setAttribute(k, v) { this[k] = v }, addEventListener() {} })
  context.document.body = { appendChild(frame) { frames.push(frame) } }
  app.initTelemetryAndLegal()
  assert.equal(frames.length, 0)
  assert.equal(overlay.style.display, 'flex')
  app.readLocal = () => 'false'
  app.initTelemetryAndLegal()
  assert.equal(frames.length, 0)
  app.readLocal = () => 'true'
  app.initTelemetryAndLegal()
  assert.equal(frames.length, 1)
  assert.equal(frames[0].sandbox, 'allow-scripts')
  assert.equal(frames[0].referrerPolicy, 'no-referrer')
  assert.equal(frames[0].src, 'https://player.test/js/telemetry.html')
})

test('event boundary drops private fields and unknown names and emits nothing before consent', () => {
  const { app } = appFixture()
  const sent = []
  app.telemetryFrame = { contentWindow: { postMessage(value) { sent.push(value) } } }
  const privateData = { filename: 'FILE_MARK.mp3', title: 'TITLE_MARK', pageUrl: 'https://host/?token=URL_MARK', referrer: 'REF_MARK', duration: 123, cuesCount: 20 }
  app.reportTelemetryEvent('open_audio', privateData)
  assert.equal(sent.length, 0)
  app.telemetryConsent = true
  for (const name of ['open_audio', 'import_ilp', 'export_ilp', 'open_lesson']) app.reportTelemetryEvent(name, privateData)
  app.reportTelemetryEvent('TITLE_MARK', privateData)
  assert.equal(sent.length, 4)
  assert.doesNotMatch(JSON.stringify(sent), /MARK|duration|cuesCount/)
})

test('playback remains synchronous when analytics is unavailable or blocked', () => {
  const { app } = appFixture()
  let unlocked = 0, played = 0
  app.unlockAudioSession = () => unlocked++
  app.cachedTelemetryBlocked = true
  app.ensureTelemetryBeforePlay(() => played++)
  assert.equal(unlocked, 1)
  assert.equal(played, 1)
})

test('course requests bypass even a populated resource cache', () => {
  const { handlers } = workerFixture({ cachedResponse: new Response('OLD_COURSE_MARK') })
  for (const [path, cache] of [['/private.ilp?token=URL_MARK', undefined], ['/api/download/123', undefined], ['/sample.ilp', 'no-store'], ['/index.html?ilppath=URL_MARK', 'no-store']]) {
    let intercepted = false
    handlers.fetch({ request: { url: 'https://player.test' + path, method: 'GET', mode: 'cors', cache }, respondWith() { intercepted = true } })
    assert.equal(intercepted, false, path)
  }
})

test('navigation cache refresh strips course query strings', async () => {
  const fetched = []
  const { handlers } = workerFixture({ cachedResponse: new Response('shell'), fetch: async request => { fetched.push(request); return new Response('shell') } })
  const background = []
  let response
  handlers.fetch({ request: { url: 'https://player.test/?ilppath=URL_MARK', method: 'GET', mode: 'navigate' }, respondWith(value) { response = value }, waitUntil(value) { background.push(value) } })
  assert.equal(await (await response).text(), 'shell')
  await Promise.all(background)
  assert.deepEqual(fetched, ['https://player.test/index.html'])
})

test('isolated SDK boundary validates the sender and reconstructs payloads', async () => {
  const html = await readFile(new URL('../js/telemetry.html', import.meta.url), 'utf8')
  const script = /<script>([\s\S]*?)<\/script>/.exec(html)[1]
  const sent = [], handlers = {}, parent = {}
  const context = vm.createContext({ parent, location: { href: 'https://player.test/js/telemetry.html' }, URL,
    window: { addEventListener(name, fn) { handlers[name] = fn }, RumSDK: { default: { sendCustom(event) { sent.push(event) } } }, LA: { track(name, properties) { sent.push({ name, properties }) } } },
    document: { addEventListener() {} }
  })
  vm.runInContext(script, context)
  const data = { type: 'il-telemetry', name: 'import_ilp', properties: { title: 'TITLE_MARK', referrer: 'REF_MARK' } }
  handlers.message({ source: {}, origin: 'https://player.test', data })
  handlers.message({ source: parent, origin: 'https://evil.test', data })
  assert.equal(sent.length, 0)
  handlers.message({ source: parent, origin: 'https://player.test', data })
  assert.equal(sent.length, 2)
  assert.doesNotMatch(JSON.stringify(sent), /MARK/)
})
