import 'ilp_models.dart';

/// A semantic passage derived from an SRT transcript.
///
/// [cueIndexes] always refer to the original SRT cue indexes. Marker cues such
/// as `Text 3` are kept out of this list so they cannot accidentally become
/// part of a listening question.
class SrtTranscriptSection {
  const SrtTranscriptSection({required this.label, required this.cueIndexes});

  final String label;
  final List<int> cueIndexes;
}

class SrtTranscriptStructure {
  const SrtTranscriptStructure._({
    required this.sections,
    required this.markerCueIndexes,
    required this.sectionIndexByCue,
  });

  static final RegExp _markerPattern = RegExp(
    r'^Text\s+([A-Za-z0-9_-]+)\s*[.。:：-]?\s*$',
    caseSensitive: false,
  );
  static final RegExp _compactMarkerPattern = RegExp(
    r'^[Tt]ext(\d+|[A-Z]{1,4})\s*[.。:：-]?\s*$',
  );
  final List<SrtTranscriptSection> sections;
  final Set<int> markerCueIndexes;
  final Map<int, int> sectionIndexByCue;

  bool get hasMarkers => markerCueIndexes.isNotEmpty;

  int? sectionIndexForCue(int cueIndex) => sectionIndexByCue[cueIndex];

  factory SrtTranscriptStructure.fromCues(
    List<SrtCue> cues, {
    bool automatic = true,
  }) {
    if (!automatic) {
      if (cues.isEmpty) {
        return const SrtTranscriptStructure._(
          sections: [],
          markerCueIndexes: {},
          sectionIndexByCue: {},
        );
      }
      return SrtTranscriptStructure._(
        sections: [
          SrtTranscriptSection(
            label: '原文',
            cueIndexes: List<int>.generate(cues.length, (index) => index),
          ),
        ],
        markerCueIndexes: const {},
        sectionIndexByCue: {
          for (var index = 0; index < cues.length; index++) index: 0,
        },
      );
    }
    final pending = <({String label, List<int> cueIndexes})>[];
    final markerCueIndexes = <int>{};
    var label = '原文';
    var currentCueIndexes = <int>[];

    void flushSection() {
      if (currentCueIndexes.isEmpty) return;
      pending.add((label: label, cueIndexes: currentCueIndexes));
      currentCueIndexes = <int>[];
    }

    for (var cueIndex = 0; cueIndex < cues.length; cueIndex++) {
      final markerLabel = _markerLabel(cues[cueIndex].text.trim());
      if (markerLabel == null) {
        currentCueIndexes.add(cueIndex);
        continue;
      }
      flushSection();
      markerCueIndexes.add(cueIndex);
      label = markerLabel;
    }
    flushSection();

    if (markerCueIndexes.isNotEmpty &&
        pending.isNotEmpty &&
        pending.first.label == '原文') {
      pending[0] = (label: '题前原文', cueIndexes: pending.first.cueIndexes);
    }

    final sections = <SrtTranscriptSection>[
      for (final item in pending)
        SrtTranscriptSection(
          label: item.label,
          cueIndexes: List.unmodifiable(item.cueIndexes),
        ),
    ];
    final sectionIndexByCue = <int, int>{};
    for (var sectionIndex = 0; sectionIndex < sections.length; sectionIndex++) {
      for (final cueIndex in sections[sectionIndex].cueIndexes) {
        sectionIndexByCue[cueIndex] = sectionIndex;
      }
    }

    return SrtTranscriptStructure._(
      sections: List.unmodifiable(sections),
      markerCueIndexes: Set.unmodifiable(markerCueIndexes),
      sectionIndexByCue: Map.unmodifiable(sectionIndexByCue),
    );
  }

  static String? _markerLabel(String text) {
    final compact = text.replaceAll(RegExp(r'\s+'), ' ').trim();
    final marker = _markerPattern.firstMatch(compact);
    if (marker != null) return 'Text ${marker.group(1)}';
    final compactMarker = _compactMarkerPattern.firstMatch(compact);
    if (compactMarker != null) return 'Text ${compactMarker.group(1)}';
    return null;
  }
}
