import { SrtParser } from './srt-parser.js';

export class LessonExercises {
  constructor({ materials = [], questions = [], clozeWordIndexes = {} } = {}) {
    this.materials = materials;
    this.questions = questions;
    this.clozeWordIndexes = clozeWordIndexes;
  }

  materialIndexForCue(cueIndex) {
    for (let i = 0; i < this.materials.length; i++) {
      const m = this.materials[i];
      if (m.cueIndexes.includes(cueIndex) || (m.leadInCueIndexes && m.leadInCueIndexes.includes(cueIndex))) {
        return i;
      }
    }
    return null;
  }

  materialForCue(cueIndex) {
    const idx = this.materialIndexForCue(cueIndex);
    return idx !== null ? this.materials[idx] : null;
  }

  materialForQuestion(questionId) {
    return this.materials.find(m => m.questionIds && m.questionIds.includes(questionId)) || null;
  }

  questionsForMaterial(material) {
    if (!material || !material.questionIds) return [];
    return this.questions.filter(q => material.questionIds.includes(q.id));
  }
}

/**
 * Normalizes exercises matching Dart LessonExercises.fromJson
 */
export function normalizeExercises(rawExercises = {}) {
  if (rawExercises instanceof LessonExercises) {
    return rawExercises;
  }

  const rawQuestions = Array.isArray(rawExercises.questions) ? rawExercises.questions : [];
  const rawMaterials = Array.isArray(rawExercises.materials) ? rawExercises.materials : [];
  const rawCloze = rawExercises.cloze || rawExercises.clozeWordIndexes || {};

  const clozeWordIndexes = {};
  for (const [cueIdxStr, indices] of Object.entries(rawCloze)) {
    const cueIdx = parseInt(cueIdxStr, 10);
    if (!isNaN(cueIdx)) {
      const arr = Array.isArray(indices) ? indices : (indices instanceof Set ? Array.from(indices) : []);
      clozeWordIndexes[cueIdx] = new Set(arr.map(i => parseInt(i, 10)).filter(i => !isNaN(i) && i >= 0));
    }
  }

  // Parse questions
  const questionsById = {};
  const decodedQuestions = [];
  for (const q of rawQuestions) {
    if (!q || typeof q.id !== 'string' || !q.id) continue;
    const question = {
      id: q.id,
      title: q.title || '',
      number: typeof q.number === 'number' ? q.number : 0,
      materialId: q.materialId || '',
      cueIndexes: Array.isArray(q.cueIndexes) ? q.cueIndexes.map(i => Math.round(i)).filter(i => i >= 0) : [],
      repeatedCueIndexes: Array.isArray(q.repeatedCueIndexes) ? q.repeatedCueIndexes.map(i => Math.round(i)).filter(i => i >= 0) : [],
      options: Array.isArray(q.options) ? q.options.filter(o => typeof o === 'string') : [],
      answerIndex: (typeof q.answerIndex === 'number' && q.answerIndex >= 0 && q.options && q.answerIndex < q.options.length) ? q.answerIndex : null
    };
    decodedQuestions.push(question);
    questionsById[question.id] = question;
  }

  const materials = [];
  const questions = [];
  const assignedCues = new Set();

  if (rawMaterials.length > 0) {
    let fallbackNumber = 1;
    for (const m of rawMaterials) {
      if (!m || !m.id || !Array.isArray(m.cueIndexes)) continue;
      const cueIndexes = m.cueIndexes.map(i => Math.round(i)).filter(i => i >= 0 && !assignedCues.has(i));
      cueIndexes.forEach(i => assignedCues.add(i));
      if (cueIndexes.length === 0) continue;

      const cueSet = new Set(cueIndexes);
      const repeatedCueIndexes = Array.isArray(m.repeatedCueIndexes)
        ? m.repeatedCueIndexes.map(i => Math.round(i)).filter(i => cueSet.has(i))
        : [];
      const leadInCueIndexes = Array.isArray(m.leadInCueIndexes)
        ? m.leadInCueIndexes.map(i => Math.round(i)).filter(i => !cueSet.has(i) && !assignedCues.has(i))
        : [];
      leadInCueIndexes.forEach(i => assignedCues.add(i));

      const questionIds = Array.isArray(m.questionIds) ? m.questionIds.filter(id => typeof id === 'string' && id.length > 0) : [];
      const acceptedIds = [];

      for (const qid of questionIds) {
        const q = questionsById[qid];
        if (!q) continue;
        acceptedIds.push(qid);
        questions.push({
          ...q,
          materialId: m.id,
          cueIndexes: [...cueIndexes],
          repeatedCueIndexes: [...repeatedCueIndexes],
          number: q.number > 0 ? q.number : fallbackNumber
        });
        fallbackNumber++;
      }

      materials.push({
        id: m.id,
        prompt: m.prompt || '',
        cueIndexes,
        repeatedCueIndexes,
        leadInCueIndexes,
        questionIds: acceptedIds
      });
    }
  } else {
    // Generate fallback materials from decoded questions
    let fallbackNumber = 1;
    for (const q of decodedQuestions) {
      const cueIndexes = q.cueIndexes.filter(i => !assignedCues.has(i));
      cueIndexes.forEach(i => assignedCues.add(i));
      if (cueIndexes.length === 0) continue;

      const cueSet = new Set(cueIndexes);
      const repeatedCueIndexes = q.repeatedCueIndexes.filter(i => cueSet.has(i));
      const materialId = q.materialId || `legacy-${q.id}`;

      materials.push({
        id: materialId,
        prompt: '',
        cueIndexes,
        repeatedCueIndexes,
        leadInCueIndexes: [],
        questionIds: [q.id]
      });

      questions.push({
        ...q,
        materialId,
        number: q.number > 0 ? q.number : fallbackNumber++
      });
    }
  }

  questions.sort((a, b) => a.number - b.number);

  return new LessonExercises({
    materials,
    questions,
    clozeWordIndexes
  });
}

/**
 * Determine mime type from file path
 * @param {string} path
 * @returns {string}
 */
export function getAudioMimeType(path = '') {
  const ext = path.split('.').pop()?.toLowerCase();
  switch (ext) {
    case 'mp3':
      return 'audio/mpeg';
    case 'wav':
      return 'audio/wav';
    case 'm4a':
    case 'mp4':
    case 'aac':
      return 'audio/mp4';
    case 'ogg':
    case 'oga':
      return 'audio/ogg';
    case 'flac':
      return 'audio/flac';
    default:
      return 'audio/mpeg';
  }
}

/**
 * Read and unpack an .ilp package from File or Blob or ArrayBuffer
 * @param {File|Blob|ArrayBuffer} fileOrBuffer
 * @returns {Promise<{manifest: any, cues: SrtCue[], audioBlob: Blob, audioUrl: string}>}
 */
export async function parseIlpPackage(fileOrBuffer) {
  if (typeof JSZip === 'undefined') {
    throw new Error('JSZip 库未加载，无法解析 .ilp 精听包');
  }

  const zip = new JSZip();
  let loadedZip;
  try {
    loadedZip = await zip.loadAsync(fileOrBuffer);
  } catch (err) {
    throw new Error('.ilp 文件不是有效的 ZIP 压缩包或已损坏: ' + (err.message || err));
  }

  const manifestFile = loadedZip.file('manifest.json');
  if (!manifestFile) {
    throw new Error('精听包缺少 manifest.json 描述文件');
  }

  let manifest;
  try {
    const manifestText = await manifestFile.async('text');
    manifest = JSON.parse(manifestText);
  } catch (err) {
    throw new Error('manifest.json 解析失败: 不是有效的 JSON 格式');
  }

  if (!manifest.title || typeof manifest.title !== 'string') {
    throw new Error('manifest.json 中缺少有效的 title');
  }

  const audioPath = manifest.audioPath || 'audio.mp3';
  const transcriptPath = manifest.transcriptPath || 'transcript.srt';

  // Find audio file in zip (case-insensitive fallback)
  let audioZipEntry = loadedZip.file(audioPath);
  if (!audioZipEntry) {
    const audioBasename = audioPath.split('/').pop().toLowerCase();
    const candidate = Object.keys(loadedZip.files).find(name => name.toLowerCase().endsWith(audioBasename));
    if (candidate) {
      audioZipEntry = loadedZip.file(candidate);
    }
  }

  if (!audioZipEntry) {
    throw new Error(`精听包中缺少音频文件: ${audioPath}`);
  }

  // Find transcript file in zip
  let srtZipEntry = loadedZip.file(transcriptPath);
  if (!srtZipEntry) {
    const srtBasename = transcriptPath.split('/').pop().toLowerCase();
    const candidate = Object.keys(loadedZip.files).find(name => name.toLowerCase().endsWith(srtBasename));
    if (candidate) {
      srtZipEntry = loadedZip.file(candidate);
    }
  }

  if (!srtZipEntry) {
    throw new Error(`精听包中缺少字幕文件: ${transcriptPath}`);
  }

  // Read transcript
  const srtText = await srtZipEntry.async('text');
  const durationSec = manifest.durationMs ? manifest.durationMs / 1000 : Infinity;
  const cues = SrtParser.parse(srtText, durationSec);

  // Read audio
  const mimeType = getAudioMimeType(audioPath);
  const audioBlob = await audioZipEntry.async('blob');
  const typedBlob = new Blob([audioBlob], { type: mimeType });
  const audioUrl = URL.createObjectURL(typedBlob);

  // Normalize exercises
  const exercises = normalizeExercises(manifest.exercises || {});

  return {
    manifest: {
      ...manifest,
      exercises
    },
    cues,
    audioBlob: typedBlob,
    audioUrl
  };
}
