import 'dart:convert';

import 'ilp_models.dart';

abstract final class SrtParser {
  static final _timePattern = RegExp(
    r'^(\d{2,}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*'
    r'(\d{2,}):(\d{2}):(\d{2})[,.](\d{3})(?:\s+.*)?$',
  );

  static List<SrtCue> parse(List<int> bytes, Duration mediaDuration) {
    late final String text;
    try {
      text = utf8.decode(bytes).replaceAll('\r\n', '\n').replaceAll('\r', '\n');
    } on FormatException {
      throw const IlpException(IlpError.invalidTranscript, 'SRT 必须使用 UTF-8 编码');
    }

    final blocks = text.trim().split(RegExp(r'\n[ \t]*\n+'));
    if (blocks.length == 1 && blocks.single.trim().isEmpty) {
      throw const IlpException(IlpError.invalidTranscript, 'SRT 不能为空');
    }

    final cues = <SrtCue>[];
    for (final block in blocks) {
      final lines = block.split('\n');
      var timeLineIndex = 0;
      if (lines.first.trim().contains(RegExp(r'^\d+$'))) timeLineIndex = 1;
      if (lines.length <= timeLineIndex + 1) {
        throw const IlpException(IlpError.invalidTranscript, 'SRT 片段缺少时间或文本');
      }
      final match = _timePattern.firstMatch(lines[timeLineIndex].trim());
      if (match == null) {
        throw const IlpException(IlpError.invalidTranscript, 'SRT 时间格式无效');
      }

      final start = _duration(match, 1);
      final end = _duration(match, 5);
      final textLines = lines.sublist(timeLineIndex + 1);
      if (textLines.any((line) => _timePattern.hasMatch(line.trim())) ||
          List.generate(textLines.length - 1, (index) => index).any(
            (index) =>
                RegExp(r'^\d+$').hasMatch(textLines[index].trim()) &&
                _timePattern.hasMatch(textLines[index + 1].trim()),
          )) {
        throw const IlpException(IlpError.invalidTranscript, 'SRT 片段之间需要空行分隔');
      }
      final cueText = textLines.join('\n').trim();
      if (cueText.isEmpty || end <= start || end > mediaDuration) {
        throw const IlpException(
          IlpError.invalidTranscript,
          'SRT 包含空文本或无效时间范围',
        );
      }
      if (cues.isNotEmpty && start < cues.last.end) {
        throw const IlpException(IlpError.invalidTranscript, 'SRT 片段时间存在重叠');
      }
      cues.add(SrtCue(start: start, end: end, text: cueText));
    }
    return List.unmodifiable(cues);
  }

  static String serialize(Iterable<SrtCue> cues) {
    final buffer = StringBuffer();
    var index = 1;
    for (final cue in cues) {
      buffer
        ..writeln(index)
        ..writeln('${_timestamp(cue.start)} --> ${_timestamp(cue.end)}')
        ..writeln(cue.text)
        ..writeln();
      index += 1;
    }
    return buffer.toString();
  }

  static String _timestamp(Duration value) {
    final hours = value.inHours.toString().padLeft(2, '0');
    final minutes = (value.inMinutes % 60).toString().padLeft(2, '0');
    final seconds = (value.inSeconds % 60).toString().padLeft(2, '0');
    final milliseconds = (value.inMilliseconds % 1000).toString().padLeft(
      3,
      '0',
    );
    return '$hours:$minutes:$seconds,$milliseconds';
  }

  static Duration _duration(RegExpMatch match, int offset) {
    final hours = int.parse(match.group(offset)!);
    final minutes = int.parse(match.group(offset + 1)!);
    final seconds = int.parse(match.group(offset + 2)!);
    final milliseconds = int.parse(match.group(offset + 3)!);
    if (minutes >= 60 || seconds >= 60) {
      throw const IlpException(IlpError.invalidTranscript, 'SRT 时间值超出范围');
    }
    return Duration(
      hours: hours,
      minutes: minutes,
      seconds: seconds,
      milliseconds: milliseconds,
    );
  }
}
