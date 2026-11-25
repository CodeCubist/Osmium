import 'dart:io';
import 'dart:typed_data';

class WakeOnLanService {
  WakeOnLanService._();

  static Uint8List? tryParseMacAddress(String? raw) {
    if (raw == null) return null;
    final clean = raw.trim().replaceAll(RegExp(r'[:.\-\s]'), '').toUpperCase();
    if (!RegExp(r'^[0-9A-F]{12}$').hasMatch(clean)) return null;
    if (clean == '000000000000' || clean == 'FFFFFFFFFFFF') return null;

    final bytes = Uint8List(6);
    for (var i = 0; i < 6; i++) {
      final parsed = int.tryParse(clean.substring(i * 2, i * 2 + 2), radix: 16);
      if (parsed == null) return null;
      bytes[i] = parsed;
    }
    if ((bytes[0] & 1) != 0) return null;
    return bytes;
  }

  static Uint8List buildMagicPacket(Uint8List macBytes) {
    if (macBytes.length != 6) {
      throw ArgumentError('MAC address must be 6 bytes');
    }
    final packet = Uint8List(102);
    for (var i = 0; i < 6; i++) {
      packet[i] = 0xFF;
    }
    for (var i = 0; i < 16; i++) {
      packet.setRange(6 + i * 6, 6 + (i + 1) * 6, macBytes);
    }
    return packet;
  }

  static bool isPrivateOrBroadcastIpv4(InternetAddress addr) {
    if (addr.type != InternetAddressType.IPv4) return false;
    final b = addr.rawAddress;
    if (b.length != 4) return false;
    if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return true;
    if (b[0] == 10) return true;
    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
    if (b[0] == 192 && b[1] == 168) return true;
    if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
    return false;
  }

  static Future<int> sendMagicPackets({
    required List<String> macAddresses,
    List<String> targetBroadcasts = const [],
    List<int> ports = const [9, 7],
    int repeatCount = 3,
  }) async {
    final validMacs = <Uint8List>[];
    final seenMacs = <String>{};
    for (final raw in macAddresses) {
      final bytes = tryParseMacAddress(raw);
      if (bytes == null) continue;
      final key = bytes
          .map((b) => b.toRadixString(16).padLeft(2, '0').toUpperCase())
          .join(':');
      if (seenMacs.add(key)) {
        validMacs.add(bytes);
        if (validMacs.length >= 6) break;
      }
    }
    if (validMacs.isEmpty) return 0;

    final validPorts = ports.where((p) => p > 0 && p <= 65535).toSet().toList();
    if (validPorts.isEmpty) {
      validPorts.addAll(const [9, 7]);
    }

    final targetIps = <String, InternetAddress>{
      '255.255.255.255': InternetAddress('255.255.255.255'),
    };
    for (final rawIp in targetBroadcasts) {
      final parsed = InternetAddress.tryParse(rawIp.trim());
      if (parsed != null && isPrivateOrBroadcastIpv4(parsed)) {
        targetIps[parsed.address] = parsed;
      }
    }

    final localBindIps = <InternetAddress>[];
    try {
      final ifaces = await NetworkInterface.list(
        type: InternetAddressType.IPv4,
        includeLoopback: false,
      );
      for (final iface in ifaces) {
        for (final addr in iface.addresses) {
          if (addr.isLoopback || addr.type != InternetAddressType.IPv4) {
            continue;
          }
          localBindIps.add(addr);
          final b = addr.rawAddress;
          if (b.length == 4) {
            final slash24 = InternetAddress('${b[0]}.${b[1]}.${b[2]}.255');
            if (isPrivateOrBroadcastIpv4(slash24)) {
              targetIps[slash24.address] = slash24;
            }
          }
        }
      }
    } catch (_) {}

    final destinations = targetIps.values.toList();
    final bursts = repeatCount.clamp(1, 5);
    var totalSent = 0;

    for (var burst = 0; burst < bursts; burst++) {
      for (final mac in validMacs) {
        final packet = buildMagicPacket(mac);

        for (final localIp in localBindIps) {
          RawDatagramSocket? sock;
          try {
            sock = await RawDatagramSocket.bind(localIp, 0);
            sock.broadcastEnabled = true;
            for (final dest in destinations) {
              for (final port in validPorts) {
                try {
                  if (sock.send(packet, dest, port) > 0) {
                    totalSent++;
                  }
                } catch (_) {}
              }
            }
          } catch (_) {
          } finally {
            sock?.close();
          }
        }

        RawDatagramSocket? anySock;
        try {
          anySock = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
          anySock.broadcastEnabled = true;
          for (final dest in destinations) {
            for (final port in validPorts) {
              try {
                if (anySock.send(packet, dest, port) > 0) {
                  totalSent++;
                }
              } catch (_) {}
            }
          }
        } catch (_) {
        } finally {
          anySock?.close();
        }
      }

      if (burst + 1 < bursts) {
        await Future<void>.delayed(const Duration(milliseconds: 80));
      }
    }

    return totalSent;
  }
}
