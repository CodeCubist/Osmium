
import 'package:flutter/material.dart';

import '../../../core/models/host_status.dart';
import '../../../core/models/osmium_settings.dart';
import '../../../core/theme/osmium_theme.dart';

class OsdCapsule extends StatelessWidget {
  const OsdCapsule({super.key, required this.level, required this.stats});

  final OsdLevel level;
  final SessionStatsSnapshot stats;

  Color _rttColor(int rtt) {
    if (rtt <= 0) return Colors.white70;
    if (rtt < 35) return const Color(0xFF6EE7A0);
    if (rtt < 85) return const Color(0xFFFBBF24);
    return const Color(0xFFFF8A8A);
  }

  @override
  Widget build(BuildContext context) {
    if (level == OsdLevel.off) return const SizedBox.shrink();

    final rttColor = _rttColor(stats.rttMs);
    final mbps = (stats.bitrateKbps / 1000.0).toStringAsFixed(1);

    return ClipRRect(
      borderRadius: BorderRadius.circular(7),
      child: BackdropFilter(
        filter: OsmiumGlass.filter(16),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4.5),
          decoration: BoxDecoration(
            color: const Color(0x730C1018),
            borderRadius: BorderRadius.circular(7),
            border: Border.all(
              color: OsmiumColors.sessionGlassBorder,
              width: 0.75,
            ),
          ),
          child: switch (level) {
        OsdLevel.compact => Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              '${stats.rttMs}ms',
              style: OsmiumTheme.monoStyle(
                fontSize: 11.5,
                fontWeight: FontWeight.w600,
                color: rttColor,
              ),
            ),
            const SizedBox(width: 8),
            Text(
              '${stats.fps} FPS',
              style: OsmiumTheme.monoStyle(
                fontSize: 11.5,
                fontWeight: FontWeight.w500,
                color: Colors.white,
              ),
            ),
          ],
        ),
        OsdLevel.standard => Text.rich(
          TextSpan(
            children: [
              TextSpan(
                text: '${stats.rttMs}ms',
                style: TextStyle(color: rttColor),
              ),
              TextSpan(
                text: ' · ${stats.fps} FPS · ${mbps}Mbps · ${stats.routeLabel}',
              ),
            ],
          ),
          style: OsmiumTheme.monoStyle(
            fontSize: 11.5,
            fontWeight: FontWeight.w500,
            color: Colors.white,
          ),
        ),
        OsdLevel.detailed => Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text.rich(
              TextSpan(
                children: [
                  TextSpan(
                    text: '${stats.rttMs}ms',
                    style: TextStyle(color: rttColor),
                  ),
                  TextSpan(
                    text: '  ${stats.fps}FPS  ${mbps}Mbps  ${stats.routeLabel}',
                  ),
                ],
              ),
              style: OsmiumTheme.monoStyle(
                fontSize: 11.5,
                fontWeight: FontWeight.w600,
                color: Colors.white,
              ),
            ),
            const SizedBox(height: 2),
            Text(
              '${stats.videoWidth}x${stats.videoHeight} ${stats.codecLabel}'
              '${stats.isHdr ? ' HDR' : ''}'
              '${stats.isYuv444 ? ' 4:4:4' : ''}'
              ' · Dec ${stats.decodeTimeMs.toStringAsFixed(1)}ms'
              ' · Loss ${stats.packetLossPercent.toStringAsFixed(1)}%',
              style: OsmiumTheme.monoStyle(
                fontSize: 10.5,
                fontWeight: FontWeight.w400,
                color: Colors.white70,
              ),
            ),
          ],
        ),
        OsdLevel.off => const SizedBox.shrink(),
      },
        ),
      ),
    );
  }
}
