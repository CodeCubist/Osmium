import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart' show Size;

import '../../../core/models/host_status.dart';
import '../../../services/session/aux_channel.dart';
import '../../../services/session/remote_session_controller.dart';

/// Immutable snapshot of what the video surface needs to know.
@immutable
class SessionVideoInfo {
  const SessionVideoInfo({this.textureId, this.size = Size.zero});

  final int? textureId;
  final Size size;

  @override
  bool operator ==(Object other) =>
      other is SessionVideoInfo &&
      other.textureId == textureId &&
      other.size == size;

  @override
  int get hashCode => Object.hash(textureId, size);
}

/// Fans the controller's single `ChangeNotifier` out into one listenable per
/// piece of UI.
///
/// The session page used to `setState` on every notification, so a stats tick
/// every 300 ms rebuilt the video surface, the touch layer and every overlay.
/// Each of those now listens only to the value it paints, and values that did
/// not actually change never notify.
class SessionSignals {
  SessionSignals(this._controller) {
    _apply();
    _controller.addListener(_apply);
  }

  final RemoteSessionController _controller;

  final ValueNotifier<SessionPhase> phase = ValueNotifier(SessionPhase.idle);
  final ValueNotifier<bool> firstFrame = ValueNotifier(false);
  final ValueNotifier<String> statusText = ValueNotifier('');
  final ValueNotifier<String?> errorMessage = ValueNotifier(null);
  final ValueNotifier<String> hostName = ValueNotifier('');
  final ValueNotifier<String?> toast = ValueNotifier(null);
  final ValueNotifier<SessionStatsSnapshot> stats = ValueNotifier(
    const SessionStatsSnapshot(),
  );
  final ValueNotifier<AuxTransferProgressEvent?> transfer = ValueNotifier(null);
  final ValueNotifier<HostSessionStatusDto> hostStatus = ValueNotifier(
    const HostSessionStatusDto(),
  );
  final ValueNotifier<HostTextFocus> textFocus = ValueNotifier(
    HostTextFocus.none,
  );
  final ValueNotifier<SessionVideoInfo> video = ValueNotifier(
    const SessionVideoInfo(),
  );

  /// Bumps for panel-level state that has no dedicated notifier (audio, mic,
  /// pointer capture, orientation, view-only, displays).
  final ValueNotifier<int> revision = ValueNotifier(0);

  Timer? _toastTimer;

  void _apply() {
    if (_disposed) return;

    final phaseNow = _controller.phase;
    if (phase.value != phaseNow) phase.value = phaseNow;

    final first = _controller.hasReceivedFirstFrame;
    if (firstFrame.value != first) firstFrame.value = first;

    final status = _controller.statusText;
    if (statusText.value != status) statusText.value = status;

    final error = _controller.errorMessage;
    if (errorMessage.value != error) errorMessage.value = error;

    final name = _controller.hostName;
    if (hostName.value != name) hostName.value = name;

    final nextToast = _controller.toastMessage;
    if (toast.value != nextToast) {
      toast.value = nextToast;
      _toastTimer?.cancel();
      if (nextToast != null) {
        _toastTimer = Timer(const Duration(seconds: 2), () {
          if (!_disposed) _controller.clearToast();
        });
      }
    }

    final nextStats = _controller.stats;
    if (!_statsEqual(stats.value, nextStats)) stats.value = nextStats;

    final nextTransfer = _controller.lastTransferProgress;
    if (!_transferEqual(transfer.value, nextTransfer)) {
      transfer.value = nextTransfer;
    }

    final nextHost = _controller.hostStatus;
    if (!_hostEqual(hostStatus.value, nextHost)) hostStatus.value = nextHost;

    final nextFocus = _controller.textFocus;
    if (textFocus.value != nextFocus) textFocus.value = nextFocus;

    final nextVideo = SessionVideoInfo(
      textureId: _controller.textureId,
      size: Size(
        (nextStats.videoWidth > 0 ? nextStats.videoWidth : nextHost.streamWidth)
            .toDouble(),
        (nextStats.videoHeight > 0
                ? nextStats.videoHeight
                : nextHost.streamHeight)
            .toDouble(),
      ),
    );
    if (video.value != nextVideo) video.value = nextVideo;

    final misc = (
      _controller.audioMuted,
      _controller.virtualSpeaker,
      _controller.micEnabled,
      _controller.pointerCaptureActive,
      _controller.orientationMode,
      _controller.viewOnly,
      nextHost.activeDisplayIndex,
      nextHost.displays.length,
    );
    if (misc != _lastMisc) {
      _lastMisc = misc;
      revision.value++;
    }
  }

  (bool, bool, bool, bool, int, bool, int, int)? _lastMisc;

  bool _disposed = false;

  void dispose() {
    if (_disposed) return;
    _disposed = true;
    _toastTimer?.cancel();
    _controller.removeListener(_apply);
    phase.dispose();
    firstFrame.dispose();
    statusText.dispose();
    errorMessage.dispose();
    hostName.dispose();
    toast.dispose();
    stats.dispose();
    transfer.dispose();
    hostStatus.dispose();
    textFocus.dispose();
    video.dispose();
    revision.dispose();
  }

  static bool _statsEqual(SessionStatsSnapshot a, SessionStatsSnapshot b) =>
      a.fps == b.fps &&
      a.rttMs == b.rttMs &&
      a.bitrateKbps == b.bitrateKbps &&
      a.packetLossPercent == b.packetLossPercent &&
      a.decodeTimeMs == b.decodeTimeMs &&
      a.videoWidth == b.videoWidth &&
      a.videoHeight == b.videoHeight &&
      a.codecId == b.codecId &&
      a.colorFlags == b.colorFlags &&
      a.transportRoute == b.transportRoute &&
      a.receivedFrames == b.receivedFrames &&
      a.droppedFrames == b.droppedFrames &&
      a.firstFrameRendered == b.firstFrameRendered &&
      a.sessionActive == b.sessionActive;

  static bool _transferEqual(
    AuxTransferProgressEvent? a,
    AuxTransferProgressEvent? b,
  ) {
    if (identical(a, b)) return true;
    if (a == null || b == null) return false;
    return a.isOutgoing == b.isOutgoing &&
        a.type == b.type &&
        a.summaryName == b.summaryName &&
        a.transferredBytes == b.transferredBytes &&
        a.totalBytes == b.totalBytes &&
        a.completed == b.completed &&
        a.failed == b.failed &&
        a.message == b.message;
  }

  static bool _hostEqual(HostSessionStatusDto a, HostSessionStatusDto b) =>
      identical(a, b) ||
      (a.activeDisplayIndex == b.activeDisplayIndex &&
          a.streamWidth == b.streamWidth &&
          a.streamHeight == b.streamHeight &&
          a.virtualSpeakerActive == b.virtualSpeakerActive &&
          a.screenLocked == b.screenLocked &&
          a.displays.length == b.displays.length &&
          a.mouseMode == b.mouseMode);
}
