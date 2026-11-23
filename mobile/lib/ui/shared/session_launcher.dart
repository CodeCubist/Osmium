import 'package:flutter/widgets.dart';

import '../../services/session/remote_session_controller.dart';

/// Opens a remote session from a tab. [sourceKey] points at the surface the
/// container-transform route grows out of (and collapses back into).
typedef SessionLauncher = void Function(
  SessionConnectRequest request, {
  GlobalKey? sourceKey,
});
