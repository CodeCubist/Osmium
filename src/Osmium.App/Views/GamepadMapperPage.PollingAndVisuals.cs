using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Osmium.App.Services;
using Osmium.Core.Interop;

namespace Osmium.App.Views;

public sealed partial class GamepadMapperPage : Page
{
    private unsafe void PollTimer_Tick(object? sender, object e)
    {
        var gp = GetSelectedGamepad();
        if (!gp.HasValue || !App.Engine.PollGamepadState(gp.Value.JoystickIndex, out var st) || st.Connected == 0)
        {
            UpdateDiagramVisuals(default, connected: false);
            return;
        }

        if (!string.IsNullOrEmpty(_listeningKey))
        {
            string? captured = TryCaptureInput(_listeningKey, in st);
            if (!string.IsNullOrEmpty(captured))
            {
                string targetKey = _listeningKey;
                _bindings[targetKey] = captured;
                _listeningKey = null;
                BindControlBar.Visibility = Visibility.Collapsed;
                UpdateBindHintText();
                UpdateAllBindingLabels();
                RebuildMappingString();

                bool applied = App.Engine.AddGamepadMapping(MappingInputBox.Text.Trim());
                string msg = LocalizationService.Format("Gamepad_BoundFmt", GetPartDisplayName(targetKey), captured);
                if (!applied && !string.IsNullOrEmpty(MappingInputBox.Text))
                {
                    msg = $"{msg} - {LocalizationService.GetString("Gamepad_ApplyFail")}";
                }

                SetStatusMessage(msg);
            }
        }

        UpdateDiagramVisuals(in st, connected: true);
    }

    private unsafe string? TryCaptureInput(string targetKey, in OsmiumGamepadLiveState st)
    {
        int newButtons = st.ButtonsMask & ~_baselineButtonsMask;
        if (newButtons != 0)
        {
            for (int b = 0; b < 32; b++)
            {
                if ((newButtons & (1 << b)) != 0)
                {
                    return $"b{b}";
                }
            }
        }

        if (!IsAxisTarget(targetKey))
        {
            byte newHat = (byte)(st.Hats[0] & ~_baselineHat0);
            if (newHat != 0)
            {
                foreach (byte dir in new byte[] { 1, 2, 4, 8 })
                {
                    if ((newHat & dir) != 0) return $"h0.{dir}";
                }
            }
        }

        for (int a = 0; a < 8; a++)
        {
            int delta = Math.Abs(st.Axes[a] - _baselineAxes[a]);
            if (delta > 14000)
            {
                if (IsAxisTarget(targetKey))
                {
                    return $"a{a}";
                }

                if (Math.Abs((int)st.Axes[a]) > 18000)
                {
                    return st.Axes[a] >= 0 ? $"+a{a}" : $"-a{a}";
                }
            }
        }

        return null;
    }

    private unsafe void UpdateDiagramVisuals(in OsmiumGamepadLiveState st, bool connected)
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        if (_lastAppliedTheme != ActualTheme)
        {
            _lastAppliedTheme = ActualTheme;
            _lastVisualStates.Clear();
        }

        Brush activeFillBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 244, 246, 250)
            : Windows.UI.Color.FromArgb(255, 24, 28, 36));
        Brush activeTextBrush = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 20, 22, 26)
            : Microsoft.UI.Colors.White);
        Brush listenBrush = ResolveThemedBrush("OsmiumBrandBrush", isDark,
            Windows.UI.Color.FromArgb(255, 0, 103, 192),
            Windows.UI.Color.FromArgb(255, 96, 205, 255));

        foreach (var (key, btn) in _partButtons)
        {
            bool isListening = string.Equals(_listeningKey, key, StringComparison.Ordinal);
            bool isPressed = connected && IsTokenActive(key, in st);
            int stateCode = isPressed ? 2 : (isListening ? 1 : 0);

            if (_lastVisualStates.TryGetValue(key, out int prevState) && prevState == stateCode)
            {
                continue;
            }

            _lastVisualStates[key] = stateCode;

            if (stateCode == 2)
            {
                btn.Background = activeFillBrush;
                btn.BorderBrush = activeFillBrush;
                SetChildTextForeground(btn, activeTextBrush);
            }
            else if (stateCode == 1)
            {
                btn.ClearValue(Control.BackgroundProperty);
                btn.BorderBrush = listenBrush;
                SetChildTextForeground(btn, null);
            }
            else
            {
                btn.ClearValue(Control.BackgroundProperty);
                btn.ClearValue(Control.BorderBrushProperty);
                SetChildTextForeground(btn, null);
            }
        }

        double lx = connected ? ReadAxisNormalized("leftx", in st) : 0;
        double ly = connected ? ReadAxisNormalized("lefty", in st) : 0;
        double rx = connected ? ReadAxisNormalized("rightx", in st) : 0;
        double ry = connected ? ReadAxisNormalized("righty", in st) : 0;

        LeftStickDotTransform.X = lx * 18.0;
        LeftStickDotTransform.Y = ly * 18.0;
        RightStickDotTransform.X = rx * 18.0;
        RightStickDotTransform.Y = ry * 18.0;

        double lt = connected ? ReadTriggerNormalized("lefttrigger", in st) : 0;
        double rt = connected ? ReadTriggerNormalized("righttrigger", in st) : 0;
        LtFillBar.Width = Math.Clamp(lt, 0, 1) * 84.0;
        RtFillBar.Width = Math.Clamp(rt, 0, 1) * 84.0;
    }

    private static void SetChildTextForeground(Button btn, Brush? brush)
    {
        if (brush == null) btn.ClearValue(Control.ForegroundProperty);
        else btn.Foreground = brush;

        void ApplyToElement(DependencyObject d)
        {
            if (d is TextBlock t)
            {
                if (brush == null) t.ClearValue(TextBlock.ForegroundProperty);
                else t.Foreground = brush;
            }
            else if (d is FontIcon fi)
            {
                if (brush == null) fi.ClearValue(FontIcon.ForegroundProperty);
                else fi.Foreground = brush;
            }
            else if (d is Panel p)
            {
                foreach (var child in p.Children) ApplyToElement(child);
            }
        }

        if (btn.Content is DependencyObject contentObj)
        {
            ApplyToElement(contentObj);
        }
    }

    private unsafe bool IsTokenActive(string key, in OsmiumGamepadLiveState st)
    {
        if (!_bindings.TryGetValue(key, out string? token) || string.IsNullOrEmpty(token)) return false;

        if (token.StartsWith("b", StringComparison.Ordinal) && int.TryParse(token[1..], out int btnIdx) &&
            btnIdx >= 0 && btnIdx < 32)
        {
            return (st.ButtonsMask & (1 << btnIdx)) != 0;
        }

        if (token.StartsWith("h0.", StringComparison.Ordinal) && byte.TryParse(token[3..], out byte hatMask))
        {
            return (st.Hats[0] & hatMask) != 0;
        }

        if (token.StartsWith("+a", StringComparison.Ordinal) && int.TryParse(token[2..], out int posAxis) &&
            posAxis >= 0 && posAxis < 8)
        {
            return st.Axes[posAxis] > 16000;
        }

        if (token.StartsWith("-a", StringComparison.Ordinal) && int.TryParse(token[2..], out int negAxis) &&
            negAxis >= 0 && negAxis < 8)
        {
            return st.Axes[negAxis] < -16000;
        }

        if (token.StartsWith("a", StringComparison.Ordinal) && int.TryParse(token[1..], out int axisIdx) &&
            axisIdx >= 0 && axisIdx < 8)
        {
            if (key is "lefttrigger" or "righttrigger")
            {
                return ReadTriggerNormalized(key, in st) > 0.25;
            }

            return Math.Abs((int)st.Axes[axisIdx]) > 14000;
        }

        return false;
    }

    private unsafe double ReadAxisNormalized(string key, in OsmiumGamepadLiveState st)
    {
        if (!_bindings.TryGetValue(key, out string? token) ||
            !token.StartsWith("a", StringComparison.Ordinal) ||
            !int.TryParse(token[1..], out int idx) ||
            idx < 0 || idx >= 8)
        {
            return 0;
        }

        return Math.Clamp(st.Axes[idx] / 32767.0, -1.0, 1.0);
    }

    private unsafe double ReadTriggerNormalized(string key, in OsmiumGamepadLiveState st)
    {
        if (!_bindings.TryGetValue(key, out string? token)) return 0;

        if (token.StartsWith("b", StringComparison.Ordinal) && int.TryParse(token[1..], out int btnIdx) &&
            btnIdx >= 0 && btnIdx < 32)
        {
            return (st.ButtonsMask & (1 << btnIdx)) != 0 ? 1.0 : 0.0;
        }

        if (token.StartsWith("a", StringComparison.Ordinal) && int.TryParse(token[1..], out int idx) &&
            idx >= 0 && idx < 8)
        {
            short raw = st.Axes[idx];
            if (_baselineAxes[idx] < -20000 || raw < -1500)
            {
                return Math.Clamp((raw + 32768) / 65535.0, 0.0, 1.0);
            }

            return Math.Clamp(Math.Abs((int)raw) / 32767.0, 0.0, 1.0);
        }

        return 0;
    }

    private void UpdateAllBindingLabels()
    {
        string TokenOrDash(string k) => _bindings.TryGetValue(k, out var t) ? t : "-";

        Tag_a.Text = TokenOrDash("a");
        Tag_b.Text = TokenOrDash("b");
        Tag_x.Text = TokenOrDash("x");
        Tag_y.Text = TokenOrDash("y");
        Tag_leftshoulder.Text = TokenOrDash("leftshoulder");
        Tag_rightshoulder.Text = TokenOrDash("rightshoulder");
        Tag_lefttrigger.Text = TokenOrDash("lefttrigger");
        Tag_righttrigger.Text = TokenOrDash("righttrigger");
        Tag_back.Text = TokenOrDash("back");
        Tag_guide.Text = TokenOrDash("guide");
        Tag_start.Text = TokenOrDash("start");
        Tag_leftstick.Text = TokenOrDash("leftstick");
        Tag_rightstick.Text = TokenOrDash("rightstick");
        Tag_leftx.Text = $"LX:{TokenOrDash("leftx")}";
        Tag_lefty.Text = $"LY:{TokenOrDash("lefty")}";
        Tag_rightx.Text = $"RX:{TokenOrDash("rightx")}";
        Tag_righty.Text = $"RY:{TokenOrDash("righty")}";
    }

    private void RebuildMappingString()
    {
        var gp = GetSelectedGamepad();
        string guid = gp?.Guid ?? "030000005e040000ea02000000000000";
        string name = (gp?.Name ?? "Xbox Controller").Replace(",", " ");
        var parts = _bindings.Select(kv => $"{kv.Key}:{kv.Value}");
        MappingInputBox.Text = $"{guid},{name},{string.Join(",", parts)},platform:Windows,";
    }

    private void ApplyMapping_Click(object sender, RoutedEventArgs e)
    {
        bool ok = App.Engine.AddGamepadMapping(MappingInputBox.Text.Trim());
        SetStatusMessage(LocalizationService.GetString(ok ? "Gamepad_ApplyOk" : "Gamepad_ApplyFail"));
    }

    private void SetStatusMessage(string msg)
    {
        MappingStatusText.Text = msg;
        MappingStatusText.Visibility = string.IsNullOrWhiteSpace(msg) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string GetPartDisplayName(string key) => key switch
    {
        "a" => LocalizationService.GetString("Gamepad_Part_A"),
        "b" => LocalizationService.GetString("Gamepad_Part_B"),
        "x" => LocalizationService.GetString("Gamepad_Part_X"),
        "y" => LocalizationService.GetString("Gamepad_Part_Y"),
        "leftshoulder" => LocalizationService.GetString("Gamepad_Part_LB"),
        "rightshoulder" => LocalizationService.GetString("Gamepad_Part_RB"),
        "lefttrigger" => LocalizationService.GetString("Gamepad_Part_LT"),
        "righttrigger" => LocalizationService.GetString("Gamepad_Part_RT"),
        "back" => LocalizationService.GetString("Gamepad_Part_Back"),
        "guide" => LocalizationService.GetString("Gamepad_Part_Guide"),
        "start" => LocalizationService.GetString("Gamepad_Part_Start"),
        "leftstick" => LocalizationService.GetString("Gamepad_Part_LS"),
        "rightstick" => LocalizationService.GetString("Gamepad_Part_RS"),
        "leftx" => LocalizationService.GetString("Gamepad_Part_LX"),
        "lefty" => LocalizationService.GetString("Gamepad_Part_LY"),
        "rightx" => LocalizationService.GetString("Gamepad_Part_RX"),
        "righty" => LocalizationService.GetString("Gamepad_Part_RY"),
        "dpup" => LocalizationService.GetString("Gamepad_Part_Up"),
        "dpdown" => LocalizationService.GetString("Gamepad_Part_Down"),
        "dpleft" => LocalizationService.GetString("Gamepad_Part_Left"),
        "dpright" => LocalizationService.GetString("Gamepad_Part_Right"),
        _ => key
    };

    private static Brush ResolveThemedBrush(string resourceKey, bool isDark, Windows.UI.Color lightFallback,
        Windows.UI.Color darkFallback)
    {
        string themeKey = isDark ? "Dark" : "Light";
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var tdObj) &&
                tdObj is ResourceDictionary td &&
                td.TryGetValue(resourceKey, out var bObj) &&
                bObj is Brush b)
            {
                return b;
            }
        }

        return new SolidColorBrush(isDark ? darkFallback : lightFallback);
    }
}