
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter/services.dart';

import '../../../core/constants/osmium_constants.dart';
import '../../../core/theme/osmium_motion.dart';
import '../../../core/theme/osmium_theme.dart';
import '../../../services/session/remote_session_controller.dart';

enum KeyboardOverlayMode { none, systemIme, pcFull }

/// Sticky modifier key bar + direct system IME bridge (with opt-in staging box)
/// + translucent PC keyboard.
class VirtualKeyboardOverlay extends StatefulWidget {
  const VirtualKeyboardOverlay({
    super.key,
    required this.mode,
    required this.opacity,
    required this.controller,
    required this.onModeChanged,
    this.visibleHeight,
  });

  final KeyboardOverlayMode mode;
  final double opacity;
  final RemoteSessionController controller;
  final ValueChanged<KeyboardOverlayMode> onModeChanged;

  /// How much of the bar currently shows above the system IME (or the screen
  /// bottom), for floating pieces that keep clear of it.
  final ValueNotifier<double>? visibleHeight;

  @override
  State<VirtualKeyboardOverlay> createState() => _VirtualKeyboardOverlayState();
}

class _VirtualKeyboardOverlayState extends State<VirtualKeyboardOverlay>
    with SingleTickerProviderStateMixin {
  final TextEditingController _imeCtrl = TextEditingController();
  final FocusNode _imeFocus = FocusNode();
  late final _DirectImeClient _directIme;

  bool _ctrlSticky = false;
  bool _altSticky = false;
  bool _shiftSticky = false;
  bool _winSticky = false;
  bool _fnRow = false;

  /// Only shown when the user explicitly toggles into staging ("待发") mode.
  bool _draftMode = false;

  /// The mode currently laid out. It lags [VirtualKeyboardOverlay.mode] during
  /// the exit animation so the keyboard can slide away intact.
  late KeyboardOverlayMode _shownMode;
  late final AnimationController _reveal;
  final GlobalKey _barKey = GlobalKey();
  double _barHeight = 0;

  @override
  void initState() {
    super.initState();
    _directIme = _DirectImeClient(
      onCommitText: _emitDirectText,
      onBackspace: (count) {
        for (var i = 0; i < count; i++) {
          _tapPcKey(WinKeys.backspace);
        }
      },
      onEnter: () => _tapPcKey(WinKeys.enter),
    );
    _shownMode = widget.mode;
    _reveal =
        AnimationController(
            vsync: this,
            duration: const Duration(milliseconds: 240),
            reverseDuration: const Duration(milliseconds: 180),
          )
          ..addListener(_publishHeight)
          ..addStatusListener((status) {
            if (status == AnimationStatus.dismissed && mounted) {
              setState(() => _shownMode = KeyboardOverlayMode.none);
            }
          });
    if (widget.mode != KeyboardOverlayMode.none) {
      _reveal.value = 1;
      if (widget.mode == KeyboardOverlayMode.systemIme) {
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted &&
              widget.mode == KeyboardOverlayMode.systemIme &&
              !_draftMode) {
            _directIme.open();
          }
        });
      }
    }
  }

  /// Slide progress: 1 fully up, 0 tucked below the bottom edge.
  double get _shown =>
      Curves.easeOutCubic.transform(_reveal.value.clamp(0.0, 1.0));

  void _publishHeight() {
    final notifier = widget.visibleHeight;
    if (notifier == null) return;
    // Reduced motion jumps the controller inside didUpdateWidget; listeners
    // elsewhere in the tree can't be dirtied mid-build, so wait for the frame.
    if (SchedulerBinding.instance.schedulerPhase ==
        SchedulerPhase.persistentCallbacks) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _publishHeight();
      });
      return;
    }
    notifier.value = _barHeight * _shown;
  }

  void _measureBar(Duration _) {
    if (!mounted) return;
    final height = _barKey.currentContext?.size?.height ?? 0;
    if (height == _barHeight) return;
    _barHeight = height;
    _publishHeight();
  }

  @override
  void didUpdateWidget(covariant VirtualKeyboardOverlay oldWidget) {
    super.didUpdateWidget(oldWidget);

    if (widget.mode != KeyboardOverlayMode.none) {
      if (_shownMode != widget.mode) {
        setState(() => _shownMode = widget.mode);
      }
      if (!_reveal.isCompleted) {
        if (OsmiumMotion.reduced(context)) {
          _reveal.value = 1;
        } else {
          _reveal.forward();
        }
      }
    } else if (oldWidget.mode != KeyboardOverlayMode.none) {
      if (_draftMode) {
        _draftMode = false;
      }
      if (OsmiumMotion.reduced(context)) {
        _reveal.value = 0;
      } else {
        _reveal.reverse();
      }
    }

    if (widget.mode == KeyboardOverlayMode.systemIme &&
        oldWidget.mode != KeyboardOverlayMode.systemIme) {
      _draftMode = false;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted || widget.mode != KeyboardOverlayMode.systemIme) return;
        if (_draftMode) {
          _imeFocus.requestFocus();
        } else {
          _directIme.open();
        }
      });
    } else if (widget.mode != KeyboardOverlayMode.systemIme &&
        oldWidget.mode == KeyboardOverlayMode.systemIme) {
      _directIme.close();
      _imeFocus.unfocus();
      _draftMode = false;
    }
  }

  @override
  void dispose() {
    _directIme.close();
    _reveal.dispose();
    _releaseAllModifiers();
    _imeCtrl.dispose();
    _imeFocus.dispose();
    super.dispose();
  }

  void _toggleDraftMode() {
    OsmiumHaptics.selection();
    final next = !_draftMode;
    setState(() => _draftMode = next);
    if (next) {
      _directIme.close();
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted &&
            widget.mode == KeyboardOverlayMode.systemIme &&
            _draftMode) {
          _imeFocus.requestFocus();
        }
      });
    } else {
      _imeFocus.unfocus();
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted &&
            widget.mode == KeyboardOverlayMode.systemIme &&
            !_draftMode) {
          _directIme.open();
        }
      });
    }
  }

  void _emitDirectText(String text) {
    if (text.isEmpty) return;
    final normalized = text.replaceAll('\r\n', '\n').replaceAll('\r', '\n');
    final parts = normalized.split('\n');
    for (var i = 0; i < parts.length; i++) {
      final chunk = parts[i];
      if (chunk.isNotEmpty) {
        if ((_ctrlSticky || _altSticky || _winSticky) && chunk.length == 1) {
          final spec = WinKeys.byLabel[chunk.toUpperCase()];
          if (spec != null) {
            _tapPcKey(spec);
          } else {
            widget.controller.sendUnicodeText(chunk);
          }
        } else {
          widget.controller.sendUnicodeText(chunk);
        }
      }
      if (i < parts.length - 1) {
        _tapPcKey(WinKeys.enter);
      }
    }
  }

  void _toggleModifier(String name) {
    OsmiumHaptics.selection();
    setState(() {
      switch (name) {
        case 'ctrl':
          _ctrlSticky = !_ctrlSticky;
          widget.controller.sendKey(WinKeys.lCtrl, _ctrlSticky);
          break;
        case 'alt':
          _altSticky = !_altSticky;
          widget.controller.sendKey(WinKeys.lAlt, _altSticky);
          break;
        case 'shift':
          _shiftSticky = !_shiftSticky;
          widget.controller.sendKey(WinKeys.lShift, _shiftSticky);
          break;
        case 'win':
          _winSticky = !_winSticky;
          widget.controller.sendKey(WinKeys.lWin, _winSticky);
          break;
      }
    });
  }

  void _releaseAllModifiers() {
    if (_ctrlSticky) widget.controller.sendKey(WinKeys.lCtrl, false);
    if (_altSticky) widget.controller.sendKey(WinKeys.lAlt, false);
    if (_shiftSticky) widget.controller.sendKey(WinKeys.lShift, false);
    if (_winSticky) widget.controller.sendKey(WinKeys.lWin, false);
    _ctrlSticky = false;
    _altSticky = false;
    _shiftSticky = false;
    _winSticky = false;
  }

  void _tapPcKey(WinKeySpec key) {
    if (key.vk == WinKeys.enter.vk) {
      OsmiumHaptics.confirm();
    } else {
      OsmiumHaptics.selection();
    }
    widget.controller.tapKey(key);
    if (_ctrlSticky || _altSticky || _shiftSticky || _winSticky) {
      Future.delayed(const Duration(milliseconds: 50), () {
        if (mounted) {
          setState(_releaseAllModifiers);
        }
      });
    }
  }

  void _submitImeText() {
    final text = _imeCtrl.text;
    if (text.isNotEmpty) {
      OsmiumHaptics.confirm();
      widget.controller.sendUnicodeText(text);
      _imeCtrl.clear();
    }
  }

  @override
  Widget build(BuildContext context) {
    WidgetsBinding.instance.addPostFrameCallback(_measureBar);
    if (_shownMode == KeyboardOverlayMode.none) return const SizedBox.shrink();

    final bottomInset = MediaQuery.viewInsetsOf(context).bottom;
    final bottomPad = MediaQuery.paddingOf(context).bottom;
    final isIme = _shownMode == KeyboardOverlayMode.systemIme;

    // Rises from the bottom edge and tucks back down below it.
    return AnimatedBuilder(
      animation: _reveal,
      builder: (context, child) {
        return Positioned(
          left: 0,
          right: 0,
          bottom: isIme ? bottomInset : 0.0,
          child: RepaintBoundary(
            child: FractionalTranslation(
              translation: Offset(0, 1 - _shown),
              child: child,
            ),
          ),
        );
      },
      child: Material(
        key: _barKey,
        color: Colors.transparent,
        child: Container(
          padding: EdgeInsets.only(
            bottom: isIme ? 6 : bottomPad + 6,
            top: 6,
            left: 8,
            right: 8,
          ),
          decoration: BoxDecoration(
            color: Color.fromRGBO(
              13,
              17,
              25,
              (widget.opacity * 0.88).clamp(0.58, 0.90),
            ),
            border: Border(
              top: BorderSide(
                color: OsmiumColors.sessionGlassBorder,
                width: 0.75,
              ),
            ),
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              // Sticky modifier & quick action strip
              SizedBox(
                height: 34,
                child: Row(
                  children: [
                    Expanded(
                      child: ListView(
                        scrollDirection: Axis.horizontal,
                        children: [
                          _KeyChip(
                            label: 'Esc',
                            onTap: () => _tapPcKey(WinKeys.esc),
                          ),
                          _KeyChip(
                            label: 'Tab',
                            onTap: () => _tapPcKey(WinKeys.tab),
                          ),
                          _KeyChip(
                            label: 'Ctrl',
                            active: _ctrlSticky,
                            onTap: () => _toggleModifier('ctrl'),
                          ),
                          _KeyChip(
                            label: 'Alt',
                            active: _altSticky,
                            onTap: () => _toggleModifier('alt'),
                          ),
                          _KeyChip(
                            label: 'Shift',
                            active: _shiftSticky,
                            onTap: () => _toggleModifier('shift'),
                          ),
                          _KeyChip(
                            label: 'Win',
                            active: _winSticky,
                            onTap: () => _toggleModifier('win'),
                          ),
                          _KeyChip(
                            label: 'Ctrl+C',
                            onTap: () => widget.controller.sendCombo([
                              WinKeys.lCtrl,
                              WinKeys.byLabel['C']!,
                            ]),
                          ),
                          _KeyChip(
                            label: 'Ctrl+V',
                            onTap: () => widget.controller.sendCombo([
                              WinKeys.lCtrl,
                              WinKeys.byLabel['V']!,
                            ]),
                          ),
                          _KeyChip(
                            label: 'Alt+Tab',
                            onTap: () => widget.controller.sendCombo([
                              WinKeys.lAlt,
                              WinKeys.tab,
                            ]),
                          ),
                          _KeyChip(
                            label: 'Del',
                            onTap: () => _tapPcKey(WinKeys.delete),
                          ),
                          _KeyChip(
                            label: isIme ? 'PC键盘' : '输入法',
                            accent: true,
                            margin: EdgeInsets.zero,
                            onTap: () => widget.onModeChanged(
                              isIme
                                  ? KeyboardOverlayMode.pcFull
                                  : KeyboardOverlayMode.systemIme,
                            ),
                          ),
                        ],
                      ),
                    ),
                    if (isIme)
                      _KeyChip(
                        label: '待发',
                        active: _draftMode,
                        margin: const EdgeInsets.only(left: 6),
                        onTap: _toggleDraftMode,
                      ),
                    // Pinned outside the scroll so it is always in reach.
                    _KeyChip(
                      label: '收起',
                      margin: const EdgeInsets.only(left: 6),
                      onTap: () =>
                          widget.onModeChanged(KeyboardOverlayMode.none),
                    ),
                  ],
                ),
              ),

              if (isIme && _draftMode) ...[
                const SizedBox(height: 6),
                Row(
                  children: [
                    Expanded(
                      child: Container(
                        height: 34,
                        padding: const EdgeInsets.symmetric(horizontal: 10),
                        decoration: BoxDecoration(
                          color: Colors.white.withValues(alpha: 0.08),
                          borderRadius: BorderRadius.circular(7),
                          border: Border.all(
                            color: OsmiumColors.sessionGlassHairline,
                            width: 0.75,
                          ),
                        ),
                        child: TextField(
                          controller: _imeCtrl,
                          focusNode: _imeFocus,
                          style: const TextStyle(
                            fontFamily: OsmiumTheme.fontFamily,
                            fontSize: 14,
                            color: Colors.white,
                          ),
                          decoration: const InputDecoration(
                            border: InputBorder.none,
                            isDense: true,
                            hintText: '待发内容',
                            hintStyle: TextStyle(
                              color: Colors.white38,
                              fontSize: 13.5,
                            ),
                          ),
                          textInputAction: TextInputAction.send,
                          onSubmitted: (_) => _submitImeText(),
                        ),
                      ),
                    ),
                    const SizedBox(width: 6),
                    _ActionPill(
                      icon: Icons.backspace_outlined,
                      onTap: () => _tapPcKey(WinKeys.backspace),
                    ),
                    const SizedBox(width: 6),
                    _ActionPill(
                      icon: Icons.keyboard_return_rounded,
                      onTap: () => _tapPcKey(WinKeys.enter),
                    ),
                    const SizedBox(width: 6),
                    _ActionPill(
                      icon: Icons.send_rounded,
                      primary: true,
                      onTap: _submitImeText,
                    ),
                  ],
                ),
              ] else if (!isIme) ...[
                const SizedBox(height: 6),
                _buildPcKeyboard(),
              ],
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildPcKeyboard() {
    final row1 = _fnRow
        ? [
            'F1',
            'F2',
            'F3',
            'F4',
            'F5',
            'F6',
            'F7',
            'F8',
            'F9',
            'F10',
            'F11',
            'F12',
          ]
        : ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '='];
    const row2 = ['Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P', '[', ']'];
    const row3 = ['A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L', ';', '\''];
    const row4 = ['Z', 'X', 'C', 'V', 'B', 'N', 'M', ',', '.', '/'];

    WinKeySpec? resolve(String k) => switch (k) {
      'F1' => WinKeys.f1,
      'F2' => WinKeys.f2,
      'F3' => WinKeys.f3,
      'F4' => WinKeys.f4,
      'F5' => WinKeys.f5,
      'F6' => WinKeys.f6,
      'F7' => WinKeys.f7,
      'F8' => WinKeys.f8,
      'F9' => WinKeys.f9,
      'F10' => WinKeys.f10,
      'F11' => WinKeys.f11,
      'F12' => WinKeys.f12,
      _ => WinKeys.byLabel[k],
    };

    Widget keyRow(List<String> keys) {
      return Padding(
        padding: const EdgeInsets.only(bottom: 4),
        child: Row(
          children: keys.map((label) {
            return Expanded(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 2),
                child: _PcKey(
                  label: label,
                  onTap: () {
                    final spec = resolve(label);
                    if (spec != null) _tapPcKey(spec);
                  },
                ),
              ),
            );
          }).toList(),
        ),
      );
    }

    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        keyRow(row1),
        keyRow(row2),
        Padding(
          padding: const EdgeInsets.only(bottom: 4),
          child: Row(
            children: [
              ...row3.map(
                (label) => Expanded(
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 2),
                    child: _PcKey(
                      label: label,
                      onTap: () {
                        final spec = resolve(label);
                        if (spec != null) _tapPcKey(spec);
                      },
                    ),
                  ),
                ),
              ),
              Expanded(
                flex: 2,
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 2),
                  child: _PcKey(
                    label: 'Enter',
                    accent: true,
                    onTap: () => _tapPcKey(WinKeys.enter),
                  ),
                ),
              ),
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.only(bottom: 4),
          child: Row(
            children: [
              Expanded(
                flex: 2,
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 2),
                  child: _PcKey(
                    label: 'Fn',
                    active: _fnRow,
                    onTap: () => setState(() => _fnRow = !_fnRow),
                  ),
                ),
              ),
              ...row4.map(
                (label) => Expanded(
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 2),
                    child: _PcKey(
                      label: label,
                      onTap: () {
                        final spec = resolve(label);
                        if (spec != null) _tapPcKey(spec);
                      },
                    ),
                  ),
                ),
              ),
              Expanded(
                flex: 2,
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 2),
                  child: _PcKey(
                    label: '⌫',
                    onTap: () => _tapPcKey(WinKeys.backspace),
                  ),
                ),
              ),
            ],
          ),
        ),
        Row(
          children: [
            Expanded(
              flex: 5,
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 2),
                child: _PcKey(
                  label: 'Space',
                  onTap: () => _tapPcKey(WinKeys.space),
                ),
              ),
            ),
            Expanded(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 2),
                child: _PcKey(
                  label: '←',
                  onTap: () => _tapPcKey(WinKeys.arrowLeft),
                ),
              ),
            ),
            Expanded(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 2),
                child: _PcKey(
                  label: '↑',
                  onTap: () => _tapPcKey(WinKeys.arrowUp),
                ),
              ),
            ),
            Expanded(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 2),
                child: _PcKey(
                  label: '↓',
                  onTap: () => _tapPcKey(WinKeys.arrowDown),
                ),
              ),
            ),
            Expanded(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 2),
                child: _PcKey(
                  label: '→',
                  onTap: () => _tapPcKey(WinKeys.arrowRight),
                ),
              ),
            ),
          ],
        ),
      ],
    );
  }
}

class _KeyChip extends StatelessWidget {
  const _KeyChip({
    required this.label,
    required this.onTap,
    this.active = false,
    this.accent = false,
    this.margin = const EdgeInsets.only(right: 6),
  });

  final String label;
  final VoidCallback onTap;
  final bool active;
  final bool accent;
  final EdgeInsetsGeometry margin;

  @override
  Widget build(BuildContext context) {
    final bg = active
        ? OsmiumColors.accentOnGlass
        : accent
        ? OsmiumColors.accentOnGlass.withValues(alpha: 0.22)
        : Colors.white.withValues(alpha: 0.12);
    final fg = active
        ? const Color(0xFF002438)
        : (accent ? OsmiumColors.accentOnGlass : Colors.white);

    return GestureDetector(
      onTap: onTap,
      child: Container(
        margin: margin,
        padding: const EdgeInsets.symmetric(horizontal: 10),
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: bg,
          borderRadius: BorderRadius.circular(6),
          border: Border.all(
            color: active
                ? OsmiumColors.accentOnGlass
                : OsmiumColors.sessionGlassHairline,
            width: 0.7,
          ),
        ),
        child: Text(
          label,
          style: TextStyle(
            fontFamily: OsmiumTheme.fontFamily,
            fontSize: 12,
            fontWeight: FontWeight.w600,
            color: fg,
          ),
        ),
      ),
    );
  }
}

class _PcKey extends StatelessWidget {
  const _PcKey({
    required this.label,
    required this.onTap,
    this.active = false,
    this.accent = false,
  });

  final String label;
  final VoidCallback onTap;
  final bool active;
  final bool accent;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        height: 34,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: active
              ? OsmiumColors.accentOnGlass
              : accent
              ? OsmiumColors.accentOnGlass.withValues(alpha: 0.22)
              : Colors.white.withValues(alpha: 0.10),
          borderRadius: BorderRadius.circular(6),
          border: Border.all(
            color: OsmiumColors.sessionGlassHairline,
            width: 0.6,
          ),
        ),
        child: Text(
          label,
          maxLines: 1,
          style: TextStyle(
            fontFamily: OsmiumTheme.fontFamily,
            fontSize: label.length > 2 ? 11 : 13,
            fontWeight: FontWeight.w500,
            color: active ? const Color(0xFF002438) : Colors.white,
          ),
        ),
      ),
    );
  }
}

class _ActionPill extends StatelessWidget {
  const _ActionPill({
    required this.icon,
    required this.onTap,
    this.primary = false,
  });

  final IconData icon;
  final VoidCallback onTap;
  final bool primary;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        width: 34,
        height: 34,
        decoration: BoxDecoration(
          color: primary
              ? OsmiumColors.accentOnGlass
              : Colors.white.withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(7),
        ),
        child: Icon(
          icon,
          size: 17,
          color: primary ? const Color(0xFF002438) : Colors.white,
        ),
      ),
    );
  }
}

/// Bridges the platform soft keyboard directly to the remote host without a
/// visible local input box.
///
/// Keeps a small buffer of zero-width-space sentinels before the caret so soft
/// Backspace always deletes a character (even on repeated holds) without
/// triggering double-space-to-period, while leaving active CJK/word composing
/// untouched until committed.
class _DirectImeClient with TextInputClient {
  _DirectImeClient({
    required this.onCommitText,
    required this.onBackspace,
    required this.onEnter,
  });

  final ValueChanged<String> onCommitText;
  final ValueChanged<int> onBackspace;
  final VoidCallback onEnter;

  static const int _sentinelCodeUnit = 0x200B;
  static const String _sentinelChar = '​';
  static const int _sentinelCount = 8;
  static final String _sentinel = _sentinelChar * _sentinelCount;
  static final TextEditingValue _idleValue = TextEditingValue(
    text: _sentinel,
    selection: const TextSelection.collapsed(offset: _sentinelCount),
  );

  static const TextInputConfiguration _config = TextInputConfiguration(
    inputType: TextInputType.multiline,
    inputAction: TextInputAction.newline,
    autocorrect: false,
    enableSuggestions: true,
    enableIMEPersonalizedLearning: false,
  );

  TextInputConnection? _connection;
  TextEditingValue _lastValue = _idleValue;
  int _anchorSentinels = _sentinelCount;
  String _sentPrefix = '';

  void open() {
    if (_connection != null && _connection!.attached) {
      _connection!.show();
      return;
    }
    _connection = TextInput.attach(this, _config);
    _resetBuffer();
    _connection!.show();
  }

  void close() {
    final conn = _connection;
    _connection = null;
    _lastValue = _idleValue;
    _anchorSentinels = _sentinelCount;
    _sentPrefix = '';
    if (conn != null && conn.attached) {
      conn.close();
    }
  }

  void _resetBuffer() {
    _lastValue = _idleValue;
    _anchorSentinels = _sentinelCount;
    _sentPrefix = '';
    final conn = _connection;
    if (conn != null && conn.attached) {
      conn.setEditingState(_idleValue);
    }
  }

  @override
  TextEditingValue? get currentTextEditingValue => _lastValue;

  @override
  AutofillScope? get currentAutofillScope => null;

  @override
  void updateEditingValue(TextEditingValue value) {
    _lastValue = value;
    final text = value.text;
    final composing = value.composing;
    final isComposing =
        composing.isValid &&
        !composing.isCollapsed &&
        composing.start >= 0 &&
        composing.end <= text.length;

    final committedEnd = isComposing ? composing.start : text.length;
    var leadingSentinels = 0;
    while (leadingSentinels < committedEnd &&
        text.codeUnitAt(leadingSentinels) == _sentinelCodeUnit) {
      leadingSentinels++;
    }

    final rawCommitted = text.substring(leadingSentinels, committedEnd);
    final committed = rawCommitted.contains(_sentinelChar)
        ? rawCommitted.replaceAll(_sentinelChar, '')
        : rawCommitted;

    var common = 0;
    final minLen = math.min(_sentPrefix.length, committed.length);
    while (common < minLen &&
        _sentPrefix.codeUnitAt(common) == committed.codeUnitAt(common)) {
      common++;
    }
    final deletedSent = _sentPrefix.length - common;
    final added = committed.substring(common);

    var deletedSentinels = 0;
    if (committed.isEmpty && !isComposing) {
      if (leadingSentinels > 0 && leadingSentinels < _anchorSentinels) {
        deletedSentinels = _anchorSentinels - leadingSentinels;
      } else if (leadingSentinels == 0 && text.isEmpty) {
        deletedSentinels = 1;
      }
    }

    final backspaces = deletedSent + deletedSentinels;
    if (backspaces > 0) {
      onBackspace(backspaces);
    }
    if (added.isNotEmpty) {
      onCommitText(added);
    }

    if (isComposing) {
      _anchorSentinels = leadingSentinels;
      _sentPrefix = committed;
    } else if (committed.isNotEmpty ||
        leadingSentinels <= 2 ||
        leadingSentinels > _sentinelCount) {
      _resetBuffer();
    } else {
      _anchorSentinels = leadingSentinels;
      _sentPrefix = '';
    }
  }

  @override
  void performAction(TextInputAction action) {
    // Flush any uncommitted composing word (e.g. underlined English text)
    // before sending Enter.
    final text = _lastValue.text;
    var leading = 0;
    while (leading < text.length &&
        text.codeUnitAt(leading) == _sentinelCodeUnit) {
      leading++;
    }
    final remaining = text.substring(leading).replaceAll(_sentinelChar, '');
    if (remaining.length > _sentPrefix.length &&
        remaining.startsWith(_sentPrefix)) {
      onCommitText(remaining.substring(_sentPrefix.length));
    } else if (remaining.isNotEmpty && _sentPrefix.isEmpty) {
      onCommitText(remaining);
    }
    onEnter();
    _resetBuffer();
  }

  @override
  void updateFloatingCursor(RawFloatingCursorPoint point) {}

  @override
  void showAutocorrectionPromptRect(int start, int end) {}

  @override
  void performPrivateCommand(String action, Map<String, dynamic> data) {}

  @override
  void connectionClosed() {
    _connection = null;
    _lastValue = _idleValue;
    _anchorSentinels = _sentinelCount;
    _sentPrefix = '';
  }
}

