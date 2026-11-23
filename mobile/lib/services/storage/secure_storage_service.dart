import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../../core/models/account_models.dart';

class SecureStorageService {
  static const _kAccountSessionKey = 'osmium_account_session_v1';

  final FlutterSecureStorage _storage = const FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );

  Future<AccountSessionData?> loadAccountSession() async {
    try {
      final raw = await _storage.read(key: _kAccountSessionKey);
      if (raw == null || raw.isEmpty) return null;
      final map = jsonDecode(raw) as Map<String, dynamic>;
      return AccountSessionData.fromStoredJson(map);
    } catch (_) {
      return null;
    }
  }

  Future<void> saveAccountSession(AccountSessionData session) async {
    await _storage.write(
      key: _kAccountSessionKey,
      value: jsonEncode(session.toJson()),
    );
  }

  Future<void> clearAccountSession() async {
    await _storage.delete(key: _kAccountSessionKey);
  }
}
