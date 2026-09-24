import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/documents/docx_text_extractor.dart';

void main() {
  test('extracts paragraphs, tables, breaks and automatic numbering', () {
    final result = const DocxTextExtractor().extractBytes(_sampleDocx());

    expect(
      result.text,
      'Listening Test\n'
      '1. First question\n'
      '2. Styled question\n'
      'Option A\tOption B\n'
      'Line one\nLine two\tAnswer',
    );
    expect(result.paragraphCount, 6);
    expect(result.tableCount, 1);
  });

  test('rejects an archive without Word document content', () {
    final archive = Archive()
      ..addFile(ArchiveFile.string('[Content_Types].xml', '<Types/>'));
    expect(
      () => const DocxTextExtractor().extractBytes(
        ZipEncoder().encodeBytes(archive),
      ),
      throwsA(isA<DocxTextExtractionException>()),
    );
  });
}

Uint8List _sampleDocx() {
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'word/document.xml',
        '''<?xml version="1.0" encoding="UTF-8"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    <w:p><w:r><w:t>Listening Test</w:t></w:r></w:p>
    <w:p>
      <w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr>
      <w:r><w:t>First question</w:t></w:r>
    </w:p>
    <w:p>
      <w:pPr><w:pStyle w:val="NumberedQuestion"/></w:pPr>
      <w:r><w:t>Styled question</w:t></w:r>
    </w:p>
    <w:tbl>
      <w:tr>
        <w:tc><w:p><w:r><w:t>Option A</w:t></w:r></w:p></w:tc>
        <w:tc><w:p><w:r><w:t>Option B</w:t></w:r></w:p></w:tc>
      </w:tr>
    </w:tbl>
    <w:p>
      <w:r><w:t>Line one</w:t><w:br/><w:t>Line two</w:t><w:tab/><w:t>Answer</w:t></w:r>
    </w:p>
  </w:body>
</w:document>''',
      ),
    )
    ..addFile(
      ArchiveFile.string(
        'word/numbering.xml',
        '''<?xml version="1.0" encoding="UTF-8"?>
<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:abstractNum w:abstractNumId="0">
    <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/></w:lvl>
  </w:abstractNum>
  <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
</w:numbering>''',
      ),
    )
    ..addFile(
      ArchiveFile.string(
        'word/styles.xml',
        '''<?xml version="1.0" encoding="UTF-8"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:style w:type="paragraph" w:styleId="NumberedQuestion">
    <w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr>
  </w:style>
</w:styles>''',
      ),
    );
  return ZipEncoder().encodeBytes(archive);
}
