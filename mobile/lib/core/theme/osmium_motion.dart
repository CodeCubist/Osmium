import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_miuix/miuix.dart';

/// Motion tokens for everything outside the remote session page.
///
/// Every spring is built from the same Folme primitives as [flutter_miuix], so
/// home surfaces and Miuix components share one physical language: quick,
/// critically damped, and interruptible. Route animations are duration driven
/// by the Flutter navigator, so [springCurve] samples the same critically
/// damped response and gives them the same feel.
class OsmiumMotion {
  OsmiumMotion._();

  // ---------------------------------------------------------------- springs

  /// Press in/out. Fast enough to feel like direct contact.
  static SpringDescription get press =>
      folmeSpring(damping: 1.0, response: 0.2);

  /// Standard settle for layout and color changes.
  static SpringDescription get standard =>
      folmeSpring(damping: 1.0, response: 0.35);

  /// Emphasised motion for entrances that should feel deliberate.
  static SpringDescription get emphasized =>
      folmeSpring(damping: 0.92, response: 0.42);

  /// Slight overshoot for small playful elements only (chips, check morphs).
  static SpringDescription get playful =>
      folmeSpring(damping: 0.68, response: 0.36);

  /// Bottom sheets: firm enter, slightly quicker exit.
  static SpringDescription get sheetEnter =>
      folmeSpring(damping: 0.92, response: 0.4);

  static SpringDescription get sheetExit =>
      folmeSpring(damping: 0.95, response: 0.28);

  // -------------------------------------------------------------- durations

  /// Route push duration. Matches the settle time of [springCurve].
  static const Duration pagePush = Duration(milliseconds: 320);
  static const Duration pagePop = Duration(milliseconds: 260);

  /// Tab fade-through.
  static const Duration tabSwitch = Duration(milliseconds: 260);

  /// Container transform into and out of a remote session.
  static const Duration containerTransform = Duration(milliseconds: 380);
  static const Duration containerTransformBack = Duration(milliseconds: 320);

  static const Duration quick = Duration(milliseconds: 160);
  static const Duration small = Duration(milliseconds: 220);

  // ----------------------------------------------------------------- curves

  /// A critically damped Folme response expressed as a [Curve].
  ///
  /// Response 0.32 s over a 320 ms route lands exactly at rest, which is what
  /// [pagePush] is timed to.
  static final Curve pushCurve = SpringCurve(response: 0.32);
  static final Curve enterCurve = SpringCurve(response: 0.4);
  static final Curve exitCurve = MiuixMotion.standardAccelerate;

  /// ------------------------------------------------------------- reduced motion

  /// True when the platform asks for reduced motion.
  static bool reduced(BuildContext context) =>
      MediaQuery.disableAnimationsOf(context);

  /// [duration] unless reduced motion is requested, in which case zero.
  static Duration durationOr(BuildContext context, Duration duration) =>
      reduced(context) ? Duration.zero : duration;

  /// Spring curve unless reduced motion is requested, in which case linear.
  static Curve curveOr(BuildContext context, Curve curve) =>
      reduced(context) ? Curves.linear : curve;
}

/// Maps the interaction classes in `agent.md` onto Android haptics.
class OsmiumHaptics {
  OsmiumHaptics._();

  /// Mirrors the user's "触觉反馈" preference. Set once from [OsmiumApp].
  static bool enabled = true;

  /// Light tap/toggle: tabs, chips, switches, copy, pickers.
  static void selection() {
    if (enabled) HapticFeedback.selectionClick();
  }

  /// Key confirmation: connect, login submit, destructive confirm.
  static void confirm() {
    if (enabled) HapticFeedback.mediumImpact();
  }

  /// Long-press lock or drag start.
  static void lock() {
    if (enabled) HapticFeedback.heavyImpact();
  }
}

/// Critically damped (or under-damped) spring step response as a [Curve].
///
/// The route navigator only understands time and curves, so this samples the
/// analytic solution of a Folme spring. [response] is the natural period in
/// seconds; the curve is normalised so `transform(1) == 1` over
/// [settleSeconds].
class SpringCurve extends Curve {
  SpringCurve({this.response = 0.32, this.damping = 1.0, double? settleSeconds})
    : settleSeconds = settleSeconds ?? response * 1.15 {
    _omega = 2 * math.pi / response;
    _end = _sample(this.settleSeconds);
  }

  final double response;
  final double damping;
  final double settleSeconds;

  late final double _omega;
  late final double _end;

  double _sample(double seconds) {
    if (seconds <= 0) return 0;
    final t = _omega * seconds;
    if (damping >= 1.0) {
      return 1 - (1 + t) * math.exp(-t);
    }
    // Under-damped: natural ring with a decaying envelope.
    final decay = math.exp(-damping * t);
    final root = math.sqrt(1 - damping * damping);
    final phase = root * t;
    return 1 - decay * (math.cos(phase) + (damping / root) * math.sin(phase));
  }

  @override
  double transformInternal(double t) {
    if (t <= 0) return 0;
    if (t >= 1) return 1;
    final value = _sample(t * settleSeconds) / _end;
    // Floating point can dip a hair below zero at the very start.
    return value.clamp(-0.25, 1.25).toDouble();
  }
}
