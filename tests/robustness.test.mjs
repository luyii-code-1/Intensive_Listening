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
  return { app, saved, timers }
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
    async match() { return undefined },
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
  handlers.fetch({ request: { url: 'https://player.test/app.js', method: 'GET', mode: 'same-origin', destination: 'script' }, respondWith(promise) { result = promise }, waitUntil(promise) { background.push(promise) } })
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
  handlers.fetch({ request: { url: 'https://player.test/app.js', method: 'GET', mode: 'same-origin' }, respondWith(promise) { result = promise }, waitUntil(promise) { background.push(promise) } })
  assert.equal(await (await result).text(), 'online')
  await Promise.all(background)
})
