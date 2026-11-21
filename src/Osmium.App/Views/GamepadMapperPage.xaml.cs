using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Osmium.App.Services;
using Osmium.Core.Interop;

namespace Osmium.App.Views;

public sealed partial class GamepadMapperPage : Page
{
    private readonly DispatcherTimer _pollTimer = new();
    private IReadOnlyList<OsmiumGamepadInfo> _gamepads = [];
    private bool _loading = true;

    private string? _listeningKey;
    private int _baselineButtonsMask;
    private readonly short[] _baselineAxes = new short[8];
    private byte _baselineHat0;

    private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _partButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastVisualStates = new(StringComparer.Ordinal);
    private ElementTheme _lastAppliedTheme = ElementTheme.Default;
    private bool _isResetDialogOpen;

    private static readonly (string Key, string DefaultToken)[] DefaultXboxLayout =
    [
        ("a", "b0"),
        ("b", "b1"),
        ("x", "b2"),
        ("y", "b3"),
        ("leftshoulder", "b4"),
        ("rightshoulder", "b5"),
        ("back", "b6"),
        ("start", "b7"),
        ("guide", "b8"),
        ("leftstick", "b9"),
        ("rightstick", "b10"),
        ("dpup", "h0.1"),
        ("dpright", "h0.2"),
        ("dpdown", "h0.4"),
        ("dpleft", "h0.8"),
        ("leftx", "a0"),
        ("lefty", "a1"),
        ("rightx", "a2"),
        ("righty", "a3"),
        ("lefttrigger", "a4"),
        ("righttrigger", "a5")
    ];

    public GamepadMapperPage()
    {
        InitializeComponent();
        BuildPartButtonIndex();
        ResetBindingsToDefault(applyToEngine: false);

        _pollTimer.Interval = TimeSpan.FromMilliseconds(25);
        _pollTimer.Tick += PollTimer_Tick;

        LocalizationService.LanguageChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyLocalization();
            RefreshList();
        });

        Loaded += (_, _) =>
        {
            _loading = true;
            _lastVisualStates.Clear();
            ApplyLocalization();
            LoadBehaviorSwitches();
            RefreshList();
            UpdateDiagramVisuals(default, connected: false);
            _loading = false;
            _pollTimer.Start();
        };
        ActualThemeChanged += (_, _) =>
        {
            _lastVisualStates.Clear();
            UpdateDiagramVisuals(default, connected: false);
        };
        Unloaded += (_, _) =>
        {
            _pollTimer.Stop();
            CancelListening(updateStatus: false);
        };
    }

    private void BuildPartButtonIndex()
    {
        _partButtons["a"] = BtnA;
        _partButtons["b"] = BtnB;
        _partButtons["x"] = BtnX;
        _partButtons["y"] = BtnY;
        _partButtons["leftshoulder"] = BtnLB;
        _partButtons["rightshoulder"] = BtnRB;
        _partButtons["lefttrigger"] = BtnLT;
        _partButtons["righttrigger"] = BtnRT;
        _partButtons["back"] = BtnBack;
        _partButtons["guide"] = BtnGuide;
        _partButtons["start"] = BtnStart;
        _partButtons["leftstick"] = BtnLS;
        _partButtons["rightstick"] = BtnRS;
        _partButtons["dpup"] = BtnDpUp;
        _partButtons["dpdown"] = BtnDpDown;
        _partButtons["dpleft"] = BtnDpLeft;
        _partButtons["dpright"] = BtnDpRight;
        _partButtons["leftx"] = BtnLeftX;
        _partButtons["lefty"] = BtnLeftY;
        _partButtons["rightx"] = BtnRightX;
        _partButtons["righty"] = BtnRightY;
    }

    private void ApplyLocalization()
    {
        GamepadTitleText.Text = LocalizationService.GetString("Gamepad_HeaderTitle");
        RefreshGamepadBtnText.Text = LocalizationService.GetString("Gamepad_RefreshBtn");
        ResetMappingBtnText.Text = LocalizationService.GetString("Gamepad_PresetXboxBtn");
        RowGamepadSelect.Title = LocalizationService.GetString("Gamepad_RowSelect");
        VisualCardTitle.Text = LocalizationService.GetString("Gamepad_VisualTitle");
        ClearBindBtn.Content = LocalizationService.GetString("Gamepad_ClearBindBtn");
        CancelBindBtn.Content = LocalizationService.GetString("Gamepad_CancelBindBtn");
        MappingPreviewLabel.Text = LocalizationService.GetString("Gamepad_AdvancedRawLabel");
        ApplyMappingBtn.Content = LocalizationService.GetString("Gamepad_ApplyBtn");
        SwapActiveBadgeText.Text = LocalizationService.GetString("Gamepad_SwapBadge");

        QuickSettingsHeaderText.Text = LocalizationService.GetString("Gamepad_QuickSettingsTitle");
        RowMultiGamepad.Title = LocalizationService.GetString("Set_MultiGamepad");
        RowSwapFace.Title = LocalizationService.GetString("Set_SwapFace");
        RowGamepadMouse.Title = LocalizationService.GetString("Set_GamepadMouse");
        RowBgGamepad.Title = LocalizationService.GetString("Set_BgGamepad");

        UpdateBindHintText();
        UpdateSelectedGamepadMeta();
        UpdateAllBindingLabels();
    }

    private void LoadBehaviorSwitches()
    {
        var s = App.Engine.SettingsService.Settings;
        MultiControllerSwitch.IsOn = s.MultiController;
        SwapFaceButtonsSwitch.IsOn = s.SwapFaceButtons;
        GamepadMouseSwitch.IsOn = s.GamepadMouse;
        BackgroundGamepadSwitch.IsOn = s.BackgroundGamepad;
        UpdateFaceSwapLabels();
    }

    private void UpdateFaceSwapLabels()
    {
        bool swap = App.Engine.SettingsService.Settings.SwapFaceButtons;
        FaceLabelA.Text = swap ? "B" : "A";
        FaceLabelB.Text = swap ? "A" : "B";
        FaceLabelX.Text = swap ? "Y" : "X";
        FaceLabelY.Text = swap ? "X" : "Y";
        SwapActiveBadge.Visibility = swap ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BehaviorSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Engine.SettingsService.Settings;
        s.MultiController = MultiControllerSwitch.IsOn;
        s.SwapFaceButtons = SwapFaceButtonsSwitch.IsOn;
        s.GamepadMouse = GamepadMouseSwitch.IsOn;
        s.BackgroundGamepad = BackgroundGamepadSwitch.IsOn;
        App.Engine.SettingsService.SaveSettings();
        UpdateFaceSwapLabels();
    }

    private void RefreshList()
    {
        _gamepads = App.Engine.GetConnectedGamepads();
        GamepadSelector.Items.Clear();
        string stdLabel = LocalizationService.GetString("Gamepad_TypeStd");
        string rawLabel = LocalizationService.GetString("Gamepad_TypeRaw");
        foreach (var gp in _gamepads)
        {
            GamepadSelector.Items.Add(new ComboBoxItem
            {
                Content = $"#{gp.JoystickIndex} {gp.Name} - {(gp.IsGameController != 0 ? stdLabel : rawLabel)}",
                Tag = gp.JoystickIndex
            });
        }

        if (GamepadSelector.Items.Count > 0)
        {
            GamepadSelector.SelectedIndex = 0;
        }

        UpdateSelectedGamepadMeta();
        RebuildMappingString();
    }

    private OsmiumGamepadInfo? GetSelectedGamepad()
    {
        int idx = GamepadSelector.SelectedIndex;
        return idx >= 0 && idx < _gamepads.Count ? _gamepads[idx] : null;
    }

    private void UpdateSelectedGamepadMeta()
    {
        var gp = GetSelectedGamepad();
        if (gp.HasValue)
        {
            var g = gp.Value;
            RowGamepadSelect.Description = LocalizationService.Format(
                "Gamepad_MetaFmt", g.Guid, g.ButtonCount, g.AxisCount, g.HatCount);
        }
        else
        {
            RowGamepadSelect.Description = LocalizationService.GetString("Gamepad_NoPadTip");
        }
    }

    private void RefreshGamepads_Click(object sender, RoutedEventArgs e)
        => RefreshList();

    private void GamepadSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelListening(updateStatus: false);
        UpdateSelectedGamepadMeta();
        RebuildMappingString();
    }

    private async void ResetMapping_Click(object sender, RoutedEventArgs e)
    {
        CancelListening(updateStatus: false);
        ResetBindingsToDefault(applyToEngine: true);
        SetStatusMessage(string.Empty);

        if (_isResetDialogOpen || XamlRoot == null) return;
        _isResetDialogOpen = true;
        try
        {
            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Gamepad_ResetDialogTitle"),
                Content = new TextBlock
                {
                    Text = LocalizationService.GetString("Gamepad_ResetOk"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MinWidth = 280
                },
                CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
                DefaultButton = ContentDialogButton.Close
            };
            await dlg.ShowAsync();
        }
        finally
        {
            _isResetDialogOpen = false;
        }
    }

    private void ResetBindingsToDefault(bool applyToEngine)
    {
        _bindings.Clear();
        foreach (var (key, token) in DefaultXboxLayout)
        {
            _bindings[key] = token;
        }

        UpdateAllBindingLabels();
        RebuildMappingString();
        if (applyToEngine && GetSelectedGamepad().HasValue)
        {
            App.Engine.AddGamepadMapping(MappingInputBox.Text.Trim());
        }
    }

    private unsafe void PadPart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;

        if (_listeningKey == key)
        {
            CancelListening(updateStatus: true);
            return;
        }

        _listeningKey = key;
        BindControlBar.Visibility = Visibility.Visible;
        UpdateBindHintText();

        var gp = GetSelectedGamepad();
        if (gp.HasValue && App.Engine.PollGamepadState(gp.Value.JoystickIndex, out var st) && st.Connected != 0)
        {
            _baselineButtonsMask = st.ButtonsMask;
            _baselineHat0 = st.Hats[0];
            for (int a = 0; a < 8; a++)
            {
                _baselineAxes[a] = st.Axes[a];
            }
        }
        else
        {
            _baselineButtonsMask = 0;
            _baselineHat0 = 0;
            Array.Clear(_baselineAxes);
        }
    }

    private void CancelBinding_Click(object sender, RoutedEventArgs e)
        => CancelListening(updateStatus: true);

    private void ClearBinding_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_listeningKey)) return;
        string targetKey = _listeningKey;
        _bindings.Remove(targetKey);
        _listeningKey = null;
        BindControlBar.Visibility = Visibility.Collapsed;
        UpdateBindHintText();
        UpdateAllBindingLabels();
        RebuildMappingString();
        SetStatusMessage(LocalizationService.Format("Gamepad_ClearedFmt", GetPartDisplayName(targetKey)));
    }

    private void CancelListening(bool updateStatus)
    {
        bool wasListening = _listeningKey != null;
        _listeningKey = null;
        if (BindControlBar != null) BindControlBar.Visibility = Visibility.Collapsed;
        UpdateBindHintText();
        if (wasListening && updateStatus)
        {
            SetStatusMessage(string.Empty);
        }
    }

    private void UpdateBindHintText()
    {
        if (BindHintText == null) return;
        BindHintText.Text = string.IsNullOrEmpty(_listeningKey)
            ? LocalizationService.GetString("Gamepad_BindHintIdle")
            : LocalizationService.Format("Gamepad_BindListening", GetPartDisplayName(_listeningKey));
    }

    private static bool IsAxisTarget(string key) =>
        key is "leftx" or "lefty" or "rightx" or "righty" or "lefttrigger" or "righttrigger";
}