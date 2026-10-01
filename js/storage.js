/**
 * IndexedDB storage for persisting imported lessons, audio data, and learning progress.
 */

const DB_NAME = 'intensive_listening_db';
const DB_VERSION = 1;
const STORE_LESSONS = 'lessons';

function toPlainJson(obj) {
  if (obj === undefined || obj === null) return obj;
  try {
    return JSON.parse(JSON.stringify(obj, (key, value) => {
      if (typeof value === 'function') return undefined;
      if (value instanceof Set) return Array.from(value);
      return value;
    }));
  } catch (_) {
    return null;
  }
}

class LessonStorage {
  constructor() {
    this._dbPromise = this._initDb();
  }

  _initDb() {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open(DB_NAME, DB_VERSION);

      request.onupgradeneeded = (event) => {
        const db = event.target.result;
        if (!db.objectStoreNames.contains(STORE_LESSONS)) {
          const store = db.createObjectStore(STORE_LESSONS, { keyPath: 'id' });
          store.createIndex('lastOpenedAt', 'lastOpenedAt', { unique: false });
        }
      };

      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }

  /**
   * Save or update a lesson
   * @param {Object} lesson
   */
  async saveLesson(lesson) {
    const db = await this._dbPromise;
    return new Promise((resolve, reject) => {
      const tx = db.transaction([STORE_LESSONS], 'readwrite');
      const store = tx.objectStore(STORE_LESSONS);

      const serialized = {
        id: lesson.id,
        title: lesson.manifest?.title || lesson.title || '',
        durationMs: lesson.manifest?.durationMs || (lesson.duration ? Math.round(lesson.duration * 1000) : 0),
        packageUuid: lesson.manifest?.packageUuid || lesson.id,
        packageVersion: lesson.manifest?.packageVersion || 1,
        audioBlob: lesson.audioBlob,
        manifestRaw: toPlainJson(lesson.manifestRaw || lesson.manifest),
        cues: toPlainJson(lesson.cues) || [],
        lastOpenedAt: lesson.lastOpenedAt || Date.now(),
        progressPosition: lesson.progressPosition || 0,
        revealedCloze: Array.from(lesson.revealedCloze || [])
      };

      const request = store.put(serialized);
      request.onsuccess = () => resolve(serialized);
      request.onerror = () => reject(request.error);
    });
  }

  /**
   * Get a lesson by ID
   * @param {string} id
   */
  async getLesson(id) {
    const db = await this._dbPromise;
    return new Promise((resolve, reject) => {
      const tx = db.transaction([STORE_LESSONS], 'readonly');
      const store = tx.objectStore(STORE_LESSONS);
      const request = store.get(id);

      request.onsuccess = () => resolve(request.result || null);
      request.onerror = () => reject(request.error);
    });
  }

  /**
   * Get all stored lessons sorted by lastOpenedAt desc
   * @returns {Promise<Array>}
   */
  async getAllLessons() {
    const db = await this._dbPromise;
    return new Promise((resolve, reject) => {
      const tx = db.transaction([STORE_LESSONS], 'readonly');
      const store = tx.objectStore(STORE_LESSONS);
      const request = store.getAll();

      request.onsuccess = () => {
        const results = request.result || [];
        results.sort((a, b) => (b.lastOpenedAt || 0) - (a.lastOpenedAt || 0));
        resolve(results);
      };
      request.onerror = () => reject(request.error);
    });
  }

  /**
   * Delete a lesson by ID
   * @param {string} id
   */
  async deleteLesson(id) {
    const db = await this._dbPromise;
    return new Promise((resolve, reject) => {
      const tx = db.transaction([STORE_LESSONS], 'readwrite');
      const store = tx.objectStore(STORE_LESSONS);
      const request = store.delete(id);

      request.onsuccess = () => resolve(true);
      request.onerror = () => reject(request.error);
    });
  }

  /**
   * Update progress for a lesson
   * @param {string} id
   * @param {{ position?: number, revealedCloze?: string[] }} progress
   */
  async updateProgress(id, progress) {
    const db = await this._dbPromise;
    return new Promise((resolve, reject) => {
      const tx = db.transaction([STORE_LESSONS], 'readwrite');
      const store = tx.objectStore(STORE_LESSONS);
      const getReq = store.get(id);

      getReq.onsuccess = () => {
        const record = getReq.result;
        if (!record) return resolve(null);

        if (typeof progress.position === 'number') {
          record.progressPosition = progress.position;
        }
        if (Array.isArray(progress.revealedCloze)) {
          record.revealedCloze = progress.revealedCloze;
        }
        record.lastOpenedAt = Date.now();

        const putReq = store.put(record);
        putReq.onsuccess = () => resolve(record);
        putReq.onerror = () => reject(putReq.error);
      };
      getReq.onerror = () => reject(getReq.error);
    });
  }
}

export const lessonStorage = new LessonStorage();
