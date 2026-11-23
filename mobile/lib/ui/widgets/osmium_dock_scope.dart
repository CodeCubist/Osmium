import 'package:flutter/widgets.dart';

/// Lets a tab tell the shell that a modal surface (dialog or action sheet) is
/// open, so the floating dock can get out of the way — and, more importantly,
/// stop intercepting taps meant for the modal's bottom edge.
class OsmiumDockScope extends InheritedNotifier<ValueNotifier<bool>> {
  const OsmiumDockScope({
    super.key,
    required ValueNotifier<bool> hidden,
    required super.child,
  }) : super(notifier: hidden);

  static ValueNotifier<bool>? maybeOf(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<OsmiumDockScope>()?.notifier;
}
