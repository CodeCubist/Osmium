import 'dart:convert';
import 'dart:io';

import 'package:shared_preferences/shared_preferences.dart';

import '../../core/models/device_identity.dart';
import '../../core/models/osmium_settings.dart';

class SettingsRepository {
  static const _kDeviceId = 'osmium_device_id';
  static const _kDeviceName = 'osmium_device_name';
  static const _kSettings = 'osmium_settings_v1';
  static const _kHistory = 'osmium_conn_history_v1';

  SharedPreferences? _prefs;

  Future<SharedPreferences> _ensurePrefs() async {
    return _prefs ??= await SharedPreferences.getInstance();
  }

  Future<DeviceIdentity> loadOrCreateIdentity() async {
    final prefs = await _ensurePrefs();
    var id = prefs.getString(_kDeviceId) ?? '';
    if (DeviceIdentity.normalizeDeviceId(id).length != 9) {
      id = DeviceIdentity.generateDeviceId();
      await prefs.setString(_kDeviceId, id);
    }
    var name = prefs.getString(_kDeviceName) ?? '';
    if (name.trim().isEmpty) {
      name = Platform.isAndroid ? 'Android Phone' : 'Osmium Mobile';
      await prefs.setString(_kDeviceName, name);
    }
    return DeviceIdentity(deviceId: id, deviceName: name);
  }

  Future<void> saveDeviceName(String name) async {
    final prefs = await _ensurePrefs();
    await prefs.setString(_kDeviceName, name.trim());
  }

  Future<OsmiumSettings> loadSettings() async {
    final prefs = await _ensurePrefs();
    final raw = prefs.getString(_kSettings);
    if (raw == null || raw.isEmpty) return const OsmiumSettings();
    try {
      final map = jsonDecode(raw) as Map<String, dynamic>;
      return OsmiumSettings.fromJson(map);
    } catch (_) {
      return const OsmiumSettings();
    }
  }

  Future<void> saveSettings(OsmiumSettings settings) async {
    final prefs = await _ensurePrefs();
    await prefs.setString(_kSettings, jsonEncode(settings.toJson()));
  }

  Future<List<ConnectionHistoryEntry>> loadHistory() async {
    final prefs = await _ensurePrefs();
    final raw = prefs.getString(_kHistory);
    if (raw == null || raw.isEmpty) return const [];
    try {
      final list = jsonDecode(raw) as List<dynamic>;
      return list
          .whereType<Map<String, dynamic>>()
          .map(ConnectionHistoryEntry.fromJson)
          .toList();
    } catch (_) {
      return const [];
    }
  }

  Future<List<ConnectionHistoryEntry>> upsertHistory(
    ConnectionHistoryEntry entry,
  ) async {
    final current = List<ConnectionHistoryEntry>.from(await loadHistory());
    current.removeWhere((e) => e.deviceId == entry.deviceId);
    current.insert(0, entry);
    if (current.length > 20) {
      current.removeRange(20, current.length);
    }
    final prefs = await _ensurePrefs();
    await prefs.setString(
      _kHistory,
      jsonEncode(current.map((e) => e.toJson()).toList()),
    );
    return current;
  }

  Future<List<ConnectionHistoryEntry>> removeHistory(String deviceId) async {
    final current = List<ConnectionHistoryEntry>.from(await loadHistory());
    current.removeWhere((e) => e.deviceId == deviceId);
    final prefs = await _ensurePrefs();
    await prefs.setString(
      _kHistory,
      jsonEncode(current.map((e) => e.toJson()).toList()),
    );
    return current;
  }
}
