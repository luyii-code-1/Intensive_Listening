/**
 * SRT Parser matching Dart SrtParser logic
 */

export class SrtCue {
  /**
   * @param {number} start Start time in seconds
   * @param {number} end End time in seconds
   * @param {string} text Subtitle text
   */
  constructor(start, end, text) {
    this.start = start;
    this.end = end;
    this.text = text;
  }
}

const TIME_PATTERN = /^(\d{2,}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2,}):(\d{2}):(\d{2})[,.](\d{3})(?:\s+.*)?$/;

export class SrtParser {
  /**
   * Parse raw SRT text into an array of SrtCue
   * @param {string} text Raw SRT string
   * @param {number} mediaDuration Duration in seconds (optional)
   * @returns {SrtCue[]}
   */
  static parse(text, mediaDuration = Infinity) {
    if (!text || !text.trim()) {
      return [];
    }

    const normalized = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    const blocks = normalized.trim().split(/\n[ \t]*\n+/);
    const cues = [];

    for (const block of blocks) {
      const lines = block.split('\n').map(l => l.trim()).filter(l => l.length > 0);
      if (lines.length < 2) continue;

      let timeLineIndex = 0;
      if (/^\d+$/.test(lines[0])) {
        timeLineIndex = 1;
      }
      if (lines.length <= timeLineIndex) continue;

      const match = lines[timeLineIndex].match(TIME_PATTERN);
      if (!match) continue;

      const start = SrtParser._parseTimestamp(match[1], match[2], match[3], match[4]);
      const end = SrtParser._parseTimestamp(match[5], match[6], match[7], match[8]);
      const textLines = lines.slice(timeLineIndex + 1);
      const cueText = textLines.join(' ').trim();

      if (!cueText || end <= start) continue;
      if (mediaDuration && mediaDuration > 0 && end > mediaDuration + 2) {
        // Tolerant clamp
      }

      cues.push(new SrtCue(start, end, cueText));
    }

    // Sort cues by start time
    cues.sort((a, b) => a.start - b.start);
    return cues;
  }

  static _parseTimestamp(h, m, s, ms) {
    return parseInt(h, 10) * 3600 +
           parseInt(m, 10) * 60 +
           parseInt(s, 10) +
           parseInt(ms, 10) / 1000;
  }

  /**
   * Format seconds to mm:ss or hh:mm:ss
   * @param {number} seconds
   * @returns {string}
   */
  static formatDuration(seconds) {
    if (isNaN(seconds) || seconds < 0) seconds = 0;
    const totalSeconds = Math.floor(seconds);
    const h = Math.floor(totalSeconds / 3600);
    const m = Math.floor((totalSeconds % 3600) / 60);
    const s = totalSeconds % 60;

    const pad = (n) => String(n).padStart(2, '0');
    if (h > 0) {
      return `${pad(h)}:${pad(m)}:${pad(s)}`;
    }
    return `${pad(m)}:${pad(s)}`;
  }
}

/**
 * Tokenize sentence text into words and punctuation
 * Regex: [A-Za-z]+(?:['’-][A-Za-z]+)*
 * Matches Dart tokenizeLessonText
 * @param {string} text
 * @returns {Array<{text: string, isWord: boolean, wordIndex?: number}>}
 */
export function tokenizeLessonText(text) {
  const parts = [];
  const regex = /[A-Za-z]+(?:['’-][A-Za-z]+)*/g;
  let cursor = 0;
  let wordIndex = 0;
  let match;

  while ((match = regex.exec(text)) !== null) {
    if (match.index > cursor) {
      parts.push({
        text: text.substring(cursor, match.index),
        isWord: false,
      });
    }
    parts.push({
      text: match[0],
      isWord: true,
      wordIndex: wordIndex++,
    });
    cursor = regex.lastIndex;
  }

  if (cursor < text.length) {
    parts.push({
      text: text.substring(cursor),
      isWord: false,
    });
  }

  return parts;
}
