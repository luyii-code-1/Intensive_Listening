import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:xml/xml.dart';

class DocxTextResult {
  const DocxTextResult({
    required this.text,
    required this.paragraphCount,
    required this.tableCount,
  });

  final String text;
  final int paragraphCount;
  final int tableCount;
}

class DocxTextExtractionException implements Exception {
  const DocxTextExtractionException(this.message);

  final String message;

  @override
  String toString() => message;
}

/// Extracts readable text from WordprocessingML `.docx` files.
///
/// Paragraphs, tables, explicit tabs/line breaks and Word list numbering are
/// retained so the generated text is suitable for exam-paper processing.
class DocxTextExtractor {
  const DocxTextExtractor();

  Future<DocxTextResult> extractFile(File file) async {
    if (!await file.exists()) {
      throw const DocxTextExtractionException('找不到指定 DOCX 文件');
    }
    return extractBytes(await file.readAsBytes());
  }

  DocxTextResult extractBytes(Uint8List bytes) {
    late final Archive archive;
    try {
      archive = ZipDecoder().decodeBytes(bytes, verify: true);
    } catch (_) {
      throw const DocxTextExtractionException('DOCX 文件已损坏或格式无效');
    }

    final documentBytes = archive.find('word/document.xml')?.readBytes();
    if (documentBytes == null) {
      throw const DocxTextExtractionException('DOCX 中缺少正文内容');
    }

    late final XmlDocument document;
    try {
      document = XmlDocument.parse(utf8.decode(documentBytes));
    } catch (_) {
      throw const DocxTextExtractionException('DOCX 正文无法解析');
    }

    final numbering = _WordNumbering.fromArchive(archive);
    final styles = _WordStyles.fromArchive(archive);
    final body = document.descendants
        .whereType<XmlElement>()
        .where((element) => element.name.local == 'body')
        .firstOrNull;
    if (body == null) {
      throw const DocxTextExtractionException('DOCX 中没有可读取的正文');
    }

    final output = <String>[];
    var paragraphCount = 0;
    var tableCount = 0;
    for (final child in body.children.whereType<XmlElement>()) {
      switch (child.name.local) {
        case 'p':
          paragraphCount += 1;
          final paragraph = _extractParagraph(child, numbering, styles);
          if (paragraph.isNotEmpty) output.add(paragraph);
        case 'tbl':
          tableCount += 1;
          final table = _extractTable(child, numbering, styles);
          paragraphCount += table.paragraphCount;
          if (table.text.isNotEmpty) output.add(table.text);
      }
    }

    final text = _normalizeOutput(output.join('\n'));
    if (text.isEmpty) {
      throw const DocxTextExtractionException('DOCX 中没有可提取的文字');
    }
    return DocxTextResult(
      text: text,
      paragraphCount: paragraphCount,
      tableCount: tableCount,
    );
  }

  _TableText _extractTable(
    XmlElement table,
    _WordNumbering numbering,
    _WordStyles styles,
  ) {
    final rows = <String>[];
    var paragraphCount = 0;
    for (final row in _directElements(table, 'tr')) {
      final cells = <String>[];
      for (final cell in _directElements(row, 'tc')) {
        final cellBlocks = <String>[];
        for (final child in cell.children.whereType<XmlElement>()) {
          if (child.name.local == 'p') {
            paragraphCount += 1;
            final text = _extractParagraph(child, numbering, styles);
            if (text.isNotEmpty) cellBlocks.add(text);
          } else if (child.name.local == 'tbl') {
            final nested = _extractTable(child, numbering, styles);
            paragraphCount += nested.paragraphCount;
            if (nested.text.isNotEmpty) cellBlocks.add(nested.text);
          }
        }
        cells.add(cellBlocks.join(' / '));
      }
      if (cells.any((cell) => cell.isNotEmpty)) rows.add(cells.join('\t'));
    }
    return _TableText(rows.join('\n'), paragraphCount);
  }

  String _extractParagraph(
    XmlElement paragraph,
    _WordNumbering numbering,
    _WordStyles styles,
  ) {
    final buffer = StringBuffer();
    final numberingReference = _paragraphNumbering(paragraph, styles);
    if (numberingReference != null) {
      final label = numbering.nextLabel(numberingReference);
      if (label.isNotEmpty) buffer.write('$label ');
    }
    _appendVisibleText(paragraph, buffer);
    return buffer.toString().replaceAll(RegExp(r'[ \t]+\n'), '\n').trim();
  }

  _NumberingReference? _paragraphNumbering(
    XmlElement paragraph,
    _WordStyles styles,
  ) {
    final properties = _firstDirectElement(paragraph, 'pPr');
    if (properties == null) return null;
    final direct = _readNumberingProperties(properties);
    if (direct != null) return direct;
    final styleId = _attributeValue(_firstDirectElement(properties, 'pStyle'));
    return styleId == null ? null : styles.numberingFor(styleId);
  }

  void _appendVisibleText(XmlNode node, StringBuffer buffer) {
    for (final child in node.children) {
      if (child is! XmlElement) continue;
      switch (child.name.local) {
        case 'del':
        case 'pPr':
          continue;
        case 't':
          buffer.write(child.innerText);
        case 'tab':
          buffer.write('\t');
        case 'br':
        case 'cr':
          buffer.write('\n');
        case 'noBreakHyphen':
          buffer.write('\u2011');
        default:
          _appendVisibleText(child, buffer);
      }
    }
  }
}

class _TableText {
  const _TableText(this.text, this.paragraphCount);

  final String text;
  final int paragraphCount;
}

class _WordStyles {
  _WordStyles(this._styles);

  factory _WordStyles.fromArchive(Archive archive) {
    final bytes = archive.find('word/styles.xml')?.readBytes();
    if (bytes == null) return _WordStyles(const {});
    try {
      final document = XmlDocument.parse(utf8.decode(bytes));
      final raw = <String, _RawStyle>{};
      for (final style in document.descendants.whereType<XmlElement>().where(
        (element) => element.name.local == 'style',
      )) {
        final id = _attributeValue(style, name: 'styleId');
        if (id == null) continue;
        final properties = _firstDirectElement(style, 'pPr');
        raw[id] = _RawStyle(
          basedOn: _attributeValue(_firstDirectElement(style, 'basedOn')),
          numbering: properties == null
              ? null
              : _readNumberingProperties(properties),
        );
      }
      final resolved = <String, _NumberingReference?>{};
      _NumberingReference? resolve(String id, Set<String> visiting) {
        if (resolved.containsKey(id)) return resolved[id];
        if (!visiting.add(id)) return null;
        final style = raw[id];
        final value =
            style?.numbering ??
            (style?.basedOn == null
                ? null
                : resolve(style!.basedOn!, visiting));
        visiting.remove(id);
        resolved[id] = value;
        return value;
      }

      for (final id in raw.keys) {
        resolve(id, <String>{});
      }
      return _WordStyles(resolved);
    } catch (_) {
      return _WordStyles(const {});
    }
  }

  final Map<String, _NumberingReference?> _styles;

  _NumberingReference? numberingFor(String styleId) => _styles[styleId];
}

class _RawStyle {
  const _RawStyle({this.basedOn, this.numbering});

  final String? basedOn;
  final _NumberingReference? numbering;
}

class _WordNumbering {
  _WordNumbering(this._schemes);

  factory _WordNumbering.fromArchive(Archive archive) {
    final bytes = archive.find('word/numbering.xml')?.readBytes();
    if (bytes == null) return _WordNumbering(const {});
    try {
      final document = XmlDocument.parse(utf8.decode(bytes));
      final abstracts = <int, Map<int, _NumberLevel>>{};
      for (final abstract in document.descendants.whereType<XmlElement>().where(
        (element) => element.name.local == 'abstractNum',
      )) {
        final abstractId = int.tryParse(
          _attributeValue(abstract, name: 'abstractNumId') ?? '',
        );
        if (abstractId == null) continue;
        final levels = <int, _NumberLevel>{};
        for (final level in _directElements(abstract, 'lvl')) {
          final index = int.tryParse(
            _attributeValue(level, name: 'ilvl') ?? '',
          );
          if (index == null) continue;
          levels[index] = _NumberLevel(
            start:
                int.tryParse(
                  _attributeValue(_firstDirectElement(level, 'start')) ?? '',
                ) ??
                1,
            format:
                _attributeValue(_firstDirectElement(level, 'numFmt')) ??
                'decimal',
            template:
                _attributeValue(_firstDirectElement(level, 'lvlText')) ??
                '%${index + 1}.',
          );
        }
        abstracts[abstractId] = levels;
      }

      final schemes = <int, Map<int, _NumberLevel>>{};
      for (final number in document.descendants.whereType<XmlElement>().where(
        (element) => element.name.local == 'num',
      )) {
        final numId = int.tryParse(
          _attributeValue(number, name: 'numId') ?? '',
        );
        final abstractId = int.tryParse(
          _attributeValue(_firstDirectElement(number, 'abstractNumId')) ?? '',
        );
        if (numId != null &&
            abstractId != null &&
            abstracts[abstractId] != null) {
          schemes[numId] = abstracts[abstractId]!;
        }
      }
      return _WordNumbering(schemes);
    } catch (_) {
      return _WordNumbering(const {});
    }
  }

  final Map<int, Map<int, _NumberLevel>> _schemes;
  final Map<int, Map<int, int>> _counters = {};

  String nextLabel(_NumberingReference reference) {
    final levels = _schemes[reference.numId];
    if (levels == null) return '';
    final level =
        levels[reference.level] ??
        _NumberLevel(
          start: 1,
          format: 'decimal',
          template: '%${reference.level + 1}.',
        );
    final counters = _counters.putIfAbsent(reference.numId, () => {});
    counters.removeWhere((index, _) => index > reference.level);
    counters[reference.level] =
        (counters[reference.level] ?? (level.start - 1)) + 1;

    var label = level.template;
    for (var index = 0; index <= 8; index++) {
      final value = counters[index];
      if (value == null) continue;
      final format = levels[index]?.format ?? 'decimal';
      label = label.replaceAll('%${index + 1}', _formatNumber(value, format));
    }
    return label.trim();
  }
}

class _NumberLevel {
  const _NumberLevel({
    required this.start,
    required this.format,
    required this.template,
  });

  final int start;
  final String format;
  final String template;
}

class _NumberingReference {
  const _NumberingReference(this.numId, this.level);

  final int numId;
  final int level;
}

_NumberingReference? _readNumberingProperties(XmlElement properties) {
  final numProperties = _firstDirectElement(properties, 'numPr');
  if (numProperties == null) return null;
  final numId = int.tryParse(
    _attributeValue(_firstDirectElement(numProperties, 'numId')) ?? '',
  );
  if (numId == null || numId <= 0) return null;
  final level =
      int.tryParse(
        _attributeValue(_firstDirectElement(numProperties, 'ilvl')) ?? '',
      ) ??
      0;
  return _NumberingReference(numId, level);
}

Iterable<XmlElement> _directElements(XmlElement parent, String localName) =>
    parent.children.whereType<XmlElement>().where(
      (element) => element.name.local == localName,
    );

XmlElement? _firstDirectElement(XmlElement parent, String localName) =>
    _directElements(parent, localName).firstOrNull;

String? _attributeValue(XmlElement? element, {String name = 'val'}) {
  if (element == null) return null;
  for (final attribute in element.attributes) {
    if (attribute.name.local == name) return attribute.value;
  }
  return null;
}

String _formatNumber(int value, String format) {
  return switch (format) {
    'lowerLetter' => _letters(value).toLowerCase(),
    'upperLetter' => _letters(value),
    'lowerRoman' => _roman(value).toLowerCase(),
    'upperRoman' => _roman(value),
    _ => '$value',
  };
}

String _letters(int value) {
  if (value <= 0) return '$value';
  final characters = <int>[];
  var current = value;
  while (current > 0) {
    current -= 1;
    characters.add(65 + current % 26);
    current ~/= 26;
  }
  return String.fromCharCodes(characters.reversed);
}

String _roman(int value) {
  if (value <= 0 || value > 3999) return '$value';
  const values = [
    (1000, 'M'),
    (900, 'CM'),
    (500, 'D'),
    (400, 'CD'),
    (100, 'C'),
    (90, 'XC'),
    (50, 'L'),
    (40, 'XL'),
    (10, 'X'),
    (9, 'IX'),
    (5, 'V'),
    (4, 'IV'),
    (1, 'I'),
  ];
  final buffer = StringBuffer();
  var remaining = value;
  for (final (number, symbol) in values) {
    while (remaining >= number) {
      buffer.write(symbol);
      remaining -= number;
    }
  }
  return buffer.toString();
}

String _normalizeOutput(String value) {
  final lines = value
      .replaceAll('\r\n', '\n')
      .replaceAll('\r', '\n')
      .split('\n');
  final output = <String>[];
  var previousBlank = false;
  for (final rawLine in lines) {
    final line = rawLine.replaceFirst(RegExp(r'[ \t]+$'), '');
    final blank = line.trim().isEmpty;
    if (blank && previousBlank) continue;
    output.add(line);
    previousBlank = blank;
  }
  return output.join('\n').trim();
}
