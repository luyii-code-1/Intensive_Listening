import 'dart:io';

import 'package:flutter/services.dart';
import 'package:http/http.dart' as http;
import 'package:http/io_client.dart';

const _globalSignRootAsset = 'assets/certificates/globalsign-root-ca-r3.pem';

Future<http.Client> createAsrHttpClient() async {
  if (!Platform.isWindows) return http.Client();

  final certificate = await rootBundle.load(_globalSignRootAsset);
  final context = SecurityContext(withTrustedRoots: true)
    ..setTrustedCertificatesBytes(
      certificate.buffer.asUint8List(
        certificate.offsetInBytes,
        certificate.lengthInBytes,
      ),
    );
  return IOClient(HttpClient(context: context));
}
