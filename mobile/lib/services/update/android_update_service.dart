import 'package:dio/dio.dart';

import '../../core/constants/osmium_constants.dart';

class AndroidUpdate {
  const AndroidUpdate(this.version, this.versionCode, this.notes, this.apkUri);

  final String version;
  final int versionCode;
  final String notes;
  final Uri? apkUri;
}

class AndroidUpdateService {
  static final _client = Dio(
    BaseOptions(
      baseUrl: OsmiumConstants.defaultApiBaseUrl,
      connectTimeout: const Duration(seconds: 5),
      receiveTimeout: const Duration(seconds: 5),
      followRedirects: false,
    ),
  );

  static Future<AndroidUpdate?> check(int installedVersionCode) async {
    final response = await _client.get<Map<String, dynamic>>(
      '/api/update',
      queryParameters: {'platform': 'android', 'lang': 'zh-CN'},
    );
    final manifest = response.data;
    final release = manifest?['data'];
    if (manifest?['ok'] != true || release is! Map<String, dynamic>) {
      return null;
    }

    final version = release['version'];
    final versionCode = release['versionCode'];
    if (version is! String ||
        !RegExp(r'^(0|[1-9]\d{0,8})(\.(0|[1-9]\d{0,8})){2}$')
            .hasMatch(version) ||
        versionCode is! int ||
        versionCode <= installedVersionCode ||
        versionCode > 2147483647) {
      return null;
    }

    final downloads = release['downloads'];
    final link = downloads is Map<String, dynamic> ? downloads['apk'] : null;
    final uri = link is String ? Uri.tryParse(link) : null;
    final notes = release['notes'];
    return AndroidUpdate(
      version,
      versionCode,
      notes is String ? notes.trim() : '',
      uri != null &&
              uri.scheme == 'https' &&
              uri.host.isNotEmpty &&
              uri.userInfo.isEmpty
          ? uri
          : null,
    );
  }
}
