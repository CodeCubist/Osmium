import 'package:flutter/material.dart';

import '../../../core/theme/osmium_motion.dart';
import '../../../core/theme/osmium_theme.dart';

/// One button on the [SessionRail].
class SessionRailAction {
  const SessionRailAction({
    required this.icon,
    required this.label,
    required this.onTap,
    this.active = false,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;
  final bool active;
}

/// Edge-docked frosted toolbar during a remote session.
///
/// Landscape docks full-height along the right edge; portrait docks full-width
/// along the bottom edge. Collapses down to a bare edge arrow when folded.
class SessionRail extends StatefulWidget {
  /// Content width in landscape (excluding right safe-area padding).
  static const double landscapeWidth = 74;

  /// Content height in portrait (excluding bottom safe-area padding).
  static const double portraitHeight = 58;

  static const Key railKey = ValueKey('session-rail');
  static const Key foldButtonKey = ValueKey('session-rail-fold');
  static const Key expandButtonKey = ValueKey('session-rail-expand');

  const SessionRail({
    super.key,
    required this.actions,
    required this.folded,
    required this.onFoldChanged,
    this.visible = true,
  });

  final List<SessionRailAction> actions;
  final bool folded;
  final ValueChanged<bool> onFoldChanged;

  /// False before streaming starts or while a keyboard overlay is up.
  final bool visible;

  @override
  State<SessionRail> createState() => _SessionRailState();
}

class _SessionRailState extends State<SessionRail>
    with SingleTickerProviderStateMixin {
  late final AnimationController _reveal;

  @override
  void initState() {
    super.initState();
    _reveal = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 220),
      reverseDuration: const Duration(milliseconds: 180),
      value: widget.visible ? 1 : 0,
    );
  }

  @override
  void didUpdateWidget(covariant SessionRail oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.visible == oldWidget.visible) return;
    if (OsmiumMotion.reduced(context)) {
      _reveal.value = widget.visible ? 1 : 0;
    } else if (widget.visible) {
      _reveal.forward();
    } else {
      _reveal.reverse();
    }
  }

  @override
  void dispose() {
    _reveal.dispose();
    super.dispose();
  }

  void _setFolded(bool folded) {
    OsmiumHaptics.selection();
    widget.onFoldChanged(folded);
  }

  @override
  Widget build(BuildContext context) {
    final screen = MediaQuery.sizeOf(context);
    final pad = MediaQuery.paddingOf(context);
    final isLandscape = screen.width > screen.height;
    final align = isLandscape ? Alignment.centerRight : Alignment.bottomCenter;
    final slideBegin = isLandscape ? const Offset(1, 0) : const Offset(0, 1);

    final switcher = AnimatedSwitcher(
      duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
      switchInCurve: Curves.easeOutCubic,
      switchOutCurve: Curves.easeInCubic,
      layoutBuilder: (current, previous) => Stack(
        alignment: align,
        children: [...previous, ?current],
      ),
      transitionBuilder: (child, animation) => FadeTransition(
        opacity: animation,
        child: SlideTransition(
          position: Tween<Offset>(
            begin: slideBegin,
            end: Offset.zero,
          ).animate(animation),
          child: child,
        ),
      ),
      child: widget.folded
          ? _FoldedArrow(
              key: ValueKey('folded-$isLandscape'),
              isLandscape: isLandscape,
              edgeInset: isLandscape ? pad.right : pad.bottom,
              onTap: () => _setFolded(false),
            )
          : _DockedRailBody(
              key: ValueKey('rail-$isLandscape'),
              isLandscape: isLandscape,
              screen: screen,
              pad: pad,
              actions: widget.actions,
              onAction: (action) {
                OsmiumHaptics.selection();
                action.onTap();
              },
              onFold: () => _setFolded(true),
            ),
    );

    final animated = AnimatedBuilder(
      animation: _reveal,
      builder: (context, child) {
        final t = Curves.easeOutCubic.transform(_reveal.value.clamp(0.0, 1.0));
        if (t == 0) return const SizedBox.shrink();
        final travel = isLandscape
            ? SessionRail.landscapeWidth + pad.right
            : SessionRail.portraitHeight + pad.bottom;
        final offset = isLandscape
            ? Offset((1 - t) * travel, 0)
            : Offset(0, (1 - t) * travel);
        return IgnorePointer(
          ignoring: t < 0.05,
          child: Opacity(
            opacity: t,
            child: Transform.translate(offset: offset, child: child),
          ),
        );
      },
      child: switcher,
    );

    return Positioned(
      key: SessionRail.railKey,
      top: isLandscape ? 0 : null,
      bottom: 0,
      left: isLandscape ? null : 0,
      right: 0,
      child: animated,
    );
  }
}

class _DockedRailBody extends StatelessWidget {
  const _DockedRailBody({
    super.key,
    required this.isLandscape,
    required this.screen,
    required this.pad,
    required this.actions,
    required this.onAction,
    required this.onFold,
  });

  final bool isLandscape;
  final Size screen;
  final EdgeInsets pad;
  final List<SessionRailAction> actions;
  final ValueChanged<SessionRailAction> onAction;
  final VoidCallback onFold;

  @override
  Widget build(BuildContext context) {
    final border = isLandscape
        ? const Border(
            left: BorderSide(color: OsmiumColors.sessionGlassBorder, width: 0.75),
          )
        : const Border(
            top: BorderSide(color: OsmiumColors.sessionGlassBorder, width: 0.75),
          );

    return ClipRect(
      child: BackdropFilter(
        filter: OsmiumGlass.filter(24),
        child: Container(
          width: isLandscape
              ? SessionRail.landscapeWidth + pad.right
              : screen.width,
          height: isLandscape
              ? screen.height
              : SessionRail.portraitHeight + pad.bottom,
          decoration: BoxDecoration(
            color: OsmiumColors.sessionGlassRailFill,
            border: border,
          ),
          child: isLandscape ? _buildLandscape() : _buildPortrait(),
        ),
      ),
    );
  }

  Widget _buildLandscape() {
    return Padding(
      padding: EdgeInsets.fromLTRB(4, pad.top, pad.right, pad.bottom),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Expanded(
            child: LayoutBuilder(
              builder: (context, constraints) {
                final buttons = [
                  for (final action in actions)
                    _RailButton(
                      action: action,
                      landscape: true,
                      onTap: () => onAction(action),
                    ),
                ];
                if (constraints.maxHeight < actions.length * 52) {
                  return SingleChildScrollView(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: buttons,
                    ),
                  );
                }
                return Column(
                  mainAxisAlignment: MainAxisAlignment.spaceEvenly,
                  children: buttons,
                );
              },
            ),
          ),
          Center(
            child: GestureDetector(
              key: SessionRail.foldButtonKey,
              behavior: HitTestBehavior.opaque,
              onTap: onFold,
              child: SizedBox(
                width: 18,
                height: 72,
                child: Icon(
                  Icons.keyboard_arrow_right_rounded,
                  size: 18,
                  color: Colors.white.withValues(alpha: 0.56),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildPortrait() {
    return Padding(
      padding: EdgeInsets.fromLTRB(pad.left + 4, 0, pad.right + 2, pad.bottom),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          for (final action in actions)
            Expanded(
              child: _RailButton(
                action: action,
                landscape: false,
                onTap: () => onAction(action),
              ),
            ),
          GestureDetector(
            key: SessionRail.foldButtonKey,
            behavior: HitTestBehavior.opaque,
            onTap: onFold,
            child: SizedBox(
              width: 28,
              height: SessionRail.portraitHeight,
              child: Icon(
                Icons.keyboard_arrow_down_rounded,
                size: 18,
                color: Colors.white.withValues(alpha: 0.56),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _RailButton extends StatefulWidget {
  const _RailButton({
    required this.action,
    required this.landscape,
    required this.onTap,
  });

  final SessionRailAction action;
  final bool landscape;
  final VoidCallback onTap;

  @override
  State<_RailButton> createState() => _RailButtonState();
}

class _RailButtonState extends State<_RailButton> {
  bool _pressed = false;

  void _press(bool down) {
    if (_pressed != down) setState(() => _pressed = down);
  }

  @override
  Widget build(BuildContext context) {
    final action = widget.action;
    final fg = action.active
        ? OsmiumColors.accentOnGlass
        : Colors.white.withValues(alpha: 0.9);
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTapDown: (_) => _press(true),
      onTapUp: (_) => _press(false),
      onTapCancel: () => _press(false),
      onTap: widget.onTap,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 120),
        height: widget.landscape ? 52 : SessionRail.portraitHeight - 8,
        margin: EdgeInsets.symmetric(horizontal: widget.landscape ? 2 : 3),
        decoration: BoxDecoration(
          color: _pressed
              ? Colors.white.withValues(alpha: 0.12)
              : Colors.transparent,
          borderRadius: BorderRadius.circular(10),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(action.icon, size: 21, color: fg),
            const SizedBox(height: 3),
            Text(
              action.label,
              maxLines: 1,
              overflow: TextOverflow.fade,
              softWrap: false,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 10.5,
                fontWeight: FontWeight.w500,
                height: 1.2,
                color: action.active
                    ? OsmiumColors.accentOnGlass
                    : Colors.white.withValues(alpha: 0.76),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _FoldedArrow extends StatelessWidget {
  const _FoldedArrow({
    super.key,
    required this.isLandscape,
    required this.edgeInset,
    required this.onTap,
  });

  final bool isLandscape;
  final double edgeInset;
  final VoidCallback onTap;

  static const _shadow = [
    Shadow(color: Color(0xA6000000), blurRadius: 8),
  ];

  @override
  Widget build(BuildContext context) {
    final icon = Icon(
      isLandscape
          ? Icons.keyboard_arrow_left_rounded
          : Icons.keyboard_arrow_up_rounded,
      size: 24,
      color: Colors.white.withValues(alpha: 0.85),
      shadows: _shadow,
    );

    if (isLandscape) {
      return Padding(
        padding: EdgeInsets.only(right: edgeInset),
        child: Center(
          child: GestureDetector(
            key: SessionRail.expandButtonKey,
            behavior: HitTestBehavior.opaque,
            onTap: onTap,
            child: SizedBox(
              width: 34,
              height: 72,
              child: Align(
                alignment: Alignment.centerRight,
                child: Padding(
                  padding: const EdgeInsets.only(right: 2),
                  child: icon,
                ),
              ),
            ),
          ),
        ),
      );
    }

    return Padding(
      padding: EdgeInsets.only(bottom: edgeInset),
      child: Center(
        child: GestureDetector(
          key: SessionRail.expandButtonKey,
          behavior: HitTestBehavior.opaque,
          onTap: onTap,
          child: SizedBox(
            width: 72,
            height: 34,
            child: Align(
              alignment: Alignment.bottomCenter,
              child: Padding(
                padding: const EdgeInsets.only(bottom: 2),
                child: icon,
              ),
            ),
          ),
        ),
      ),
    );
  }
}
