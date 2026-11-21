using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ShapesPath = Microsoft.UI.Xaml.Shapes.Path;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.Core.Interop;
using Osmium.Core.Models;
using Osmium.Core.Network;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Osmium.App.Views;

public sealed partial class RemoteSessionWindow : Window
{
    private void RebuildDisplayTabsIfChanged()
    {
        var displays = _hostStatus.Displays;
        bool same = displays.Count == _renderedTabs.Count;
        if (same)
        {
            for (int i = 0; i < displays.Count; i++)
            {
                if (displays[i].Index != _renderedTabs[i].Index ||
                    displays[i].Width != _renderedTabs[i].Width ||
                    displays[i].Height != _renderedTabs[i].Height ||
                    displays[i].IsPrimary != _renderedTabs[i].IsPrimary ||
                    !string.Equals(displays[i].DeviceName, _renderedTabs[i].DeviceName, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
        }

        if (same)
        {
            UpdateTabSelectionStyles();
            return;
        }

        _renderedTabs = displays.ToList();
        _tabVisuals.Clear();
        DisplayTabsPanel.Children.Clear();

        foreach (var d in _renderedTabs)
        {
            var tabRoot = new Grid
            {
                Tag = d.Index,
                Height = 32,
                Margin = new Thickness(0, 0, -4, 0),
                VerticalAlignment = VerticalAlignment.Top
            };

            var fillPath = new ShapesPath
            {
                StrokeThickness = 0,
                IsHitTestVisible = false
            };

            var strokePath = new ShapesPath
            {
                StrokeThickness = 1,
                IsHitTestVisible = false
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            var icon = new FontIcon
            {
                FontFamily = (FontFamily)Application.Current.Resources["FluentIconsFont"],
                Glyph = "\uF35A",
                FontSize = 13
            };
            row.Children.Add(icon);

            string label = !string.IsNullOrWhiteSpace(d.DeviceName)
                ? d.DeviceName
                : (!string.IsNullOrWhiteSpace(d.Name) ? d.Name : $"DISPLAY{d.Index + 1}");

            var titleTb = new TextBlock
            {
                Text = label,
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(tabRoot, $"{label} - {d.Width}×{d.Height} @ {d.RefreshRate}Hz");
            row.Children.Add(titleTb);

            var switchBtn = new Button
            {
                Content = row,
                Tag = d.Index,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            switchBtn.Click += DisplayTabSwitch_Click;

            var outer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(16, 0, 16, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            outer.Children.Add(switchBtn);

            tabRoot.Children.Add(fillPath);
            tabRoot.Children.Add(strokePath);
            tabRoot.Children.Add(outer);

            var visual = new DisplayTabVisual
            {
                DisplayIndex = d.Index,
                RootGrid = tabRoot,
                FillPath = fillPath,
                StrokePath = strokePath,
                ContentPanel = outer,
                Icon = icon,
                TitleBlock = titleTb
            };
            tabRoot.SizeChanged += (_, _) => UpdateTabPathGeometries(visual);
            tabRoot.PointerEntered += (_, _) =>
            {
                visual.IsHovered = true;
                UpdateTabSelectionStyles();
            };
            tabRoot.PointerExited += (_, _) =>
            {
                visual.IsHovered = false;
                UpdateTabSelectionStyles();
            };

            _tabVisuals.Add(visual);
            DisplayTabsPanel.Children.Add(tabRoot);

            var sep = new Border
            {
                Tag = ("sep", d.Index),
                Width = 1,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(3, 9, -1, 0),
                Background = ResolveDividerBrush()
            };
            DisplayTabsPanel.Children.Add(sep);
        }

        UpdateTabSelectionStyles();
        DispatcherQueue.TryEnqueue(UpdateTitleBarPassthroughRegions);
    }

    private void UpdateBrandChipGeometry()
    {
        double w = BrandChipHost.ActualWidth;
        double h = BrandChipHost.ActualHeight;
        if (w < 24 || h < 16) return;

        BrandChipFill.Data = BuildTopLeftBrandGeometry(w, h, 8.0, 8.0, closed: true, inset: 0.0);
        BrandChipStroke.Data = BuildTopLeftBrandGeometry(w, h, 8.0, 8.0, closed: false, inset: 0.5);
    }

    private static Geometry BuildTopLeftBrandGeometry(double w, double h, double skirtR, double cornerR, bool closed,
        double inset)
    {
        double sR = Math.Min(skirtR, Math.Min(w * 0.25, h * 0.35));
        double cR = Math.Min(cornerR, Math.Min(w * 0.25, h * 0.45));
        double right = Math.Max(sR + cR + 4.0, w - inset);
        double bottom = h - inset;
        double top = closed ? 0.0 : inset;

        var fig = new PathFigure
        {
            StartPoint = closed ? new Point(0, 0) : new Point(0, bottom),
            IsClosed = closed,
            IsFilled = closed
        };
        if (closed)
        {
            fig.Segments.Add(new LineSegment { Point = new Point(0, bottom) });
        }

        fig.Segments.Add(new LineSegment { Point = new Point(right - sR - cR, bottom) });
        fig.Segments.Add(new ArcSegment
        {
            Point = new Point(right - sR, bottom - cR),
            Size = new Size(cR, cR),
            SweepDirection = SweepDirection.Counterclockwise,
            IsLargeArc = false
        });
        fig.Segments.Add(new LineSegment { Point = new Point(right - sR, top + sR) });
        fig.Segments.Add(new ArcSegment
        {
            Point = new Point(right, top),
            Size = new Size(sR, sR),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = false
        });

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    private static void UpdateTabPathGeometries(DisplayTabVisual visual)
    {
        double w = visual.RootGrid.ActualWidth;
        double h = double.IsNaN(visual.RootGrid.Height) ? visual.RootGrid.ActualHeight : visual.RootGrid.Height;
        if (w < 24 || h < 16) return;

        visual.FillPath.Data = BuildTopHangingTabGeometry(w, h, 8.0, 8.0, closed: true, inset: 0.0);
        visual.StrokePath.Data = BuildTopHangingTabGeometry(w, h, 8.0, 8.0, closed: false, inset: 0.5);
    }

    private static Geometry BuildTopHangingTabGeometry(double w, double h, double skirtR, double cornerR, bool closed,
        double inset)
    {
        double left = inset;
        double top = closed ? 0.0 : inset;
        double right = Math.Max(left + 28.0, w - inset);
        double bottom = h - inset;
        double sR = Math.Min(skirtR, Math.Min((right - left) * 0.16, (bottom - top) * 0.35));
        double cR = Math.Min(cornerR, Math.Min((right - left) * 0.22, (bottom - top) * 0.45));

        var fig = new PathFigure
        {
            StartPoint = new Point(left, top),
            IsClosed = closed,
            IsFilled = closed
        };
        fig.Segments.Add(new ArcSegment
        {
            Point = new Point(left + sR, top + sR),
            Size = new Size(sR, sR),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = false
        });
        fig.Segments.Add(new LineSegment { Point = new Point(left + sR, bottom - cR) });
        fig.Segments.Add(new ArcSegment
        {
            Point = new Point(left + sR + cR, bottom),
            Size = new Size(cR, cR),
            SweepDirection = SweepDirection.Counterclockwise,
            IsLargeArc = false
        });
        fig.Segments.Add(new LineSegment { Point = new Point(right - sR - cR, bottom) });
        fig.Segments.Add(new ArcSegment
        {
            Point = new Point(right - sR, bottom - cR),
            Size = new Size(cR, cR),
            SweepDirection = SweepDirection.Counterclockwise,
            IsLargeArc = false
        });
        fig.Segments.Add(new LineSegment { Point = new Point(right - sR, top + sR) });
        fig.Segments.Add(new ArcSegment
        {
            Point = new Point(right, top),
            Size = new Size(sR, sR),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = false
        });

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    private Brush ResolveDividerBrush()
        => ResolveThemeBrush("OsmiumDividerBrush", Windows.UI.Color.FromArgb(0x10, 0, 0, 0),
            Windows.UI.Color.FromArgb(0x18, 255, 255, 255));

    private Brush ResolveThemeBrush(string key, Windows.UI.Color fallbackLight, Windows.UI.Color fallbackDark)
    {
        bool isDark = RootSessionGrid.ActualTheme == ElementTheme.Dark;
        string themeKey = isDark ? "Dark" : "Light";
        foreach (var md in Application.Current.Resources.MergedDictionaries)
        {
            if (md.ThemeDictionaries.TryGetValue(themeKey, out var tdObj) &&
                tdObj is ResourceDictionary td &&
                td.TryGetValue(key, out var val) &&
                val is Brush b)
            {
                return b;
            }
        }

        if (Application.Current.Resources.TryGetValue(key, out var topVal) && topVal is Brush topBrush)
        {
            return topBrush;
        }

        return new SolidColorBrush(isDark ? fallbackDark : fallbackLight);
    }

    private void ApplyTitleBarChromeTheme()
    {
        bool isDark = RootSessionGrid.ActualTheme == ElementTheme.Dark;
        if (_isFullscreen)
        {
            AppTitleBar.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x8C, 0x14, 0x18, 0x21));
            TitleBarBottomDivider.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            BrandChipFill.Fill = new SolidColorBrush(Colors.Transparent);
            BrandChipStroke.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            RemoteHostTitleText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0xF8, 0xFA, 0xFC));
        }
        else
        {
            AppTitleBar.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x01, 0, 0, 0));
            TitleBarBottomDivider.Background = ResolveDividerBrush();
            BrandChipFill.Fill = new SolidColorBrush(Colors.Transparent);
            BrandChipStroke.Stroke = new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x16, 0, 0, 0));
            RemoteHostTitleText.Foreground = ResolveThemeBrush(
                "TextFillColorPrimaryBrush",
                Windows.UI.Color.FromArgb(0xFF, 0x18, 0x1C, 0x24),
                Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF6, 0xFA));
        }
    }

    private void UpdateTabSelectionStyles()
    {
        bool isDark = RootSessionGrid.ActualTheme == ElementTheme.Dark;
        Brush activeBg;
        Brush hoverBg;
        Brush activeBorder;
        Brush dividerBrush;
        Brush activeFg;
        Brush hoverFg;
        Brush inactiveFg;

        if (_isFullscreen)
        {
            activeBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0x24, 0x2C, 0x3A));
            hoverBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2C, 0x00, 0x00, 0x00));
            activeBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
            dividerBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            activeFg = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF8, 0xFA, 0xFC));
            hoverFg = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));
            inactiveFg = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD8, 0xE2, 0xE8, 0xF0));
        }
        else
        {
            activeBg = new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF));
            hoverBg = new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x12, 0x00, 0x00, 0x00));
            activeBorder = new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x22, 0, 0, 0));
            dividerBrush = ResolveDividerBrush();
            activeFg = new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(0xFF, 0xF8, 0xFA, 0xFC)
                : Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A));
            hoverFg = new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0xE6, 0x0F, 0x17, 0x2A));
            inactiveFg = new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(0xB8, 0xE2, 0xE8, 0xF0)
                : Windows.UI.Color.FromArgb(0xB8, 0x33, 0x41, 0x55));
        }

        var transparent = new SolidColorBrush(Colors.Transparent);
        int activeIdx = _hostStatus.ActiveDisplayIndex;

        foreach (var v in _tabVisuals)
        {
            bool active = v.DisplayIndex == activeIdx;
            double targetH = active ? 38.0 : 32.0;
            bool heightChanged = Math.Abs(v.RootGrid.Height - targetH) > 0.1;
            if (heightChanged)
            {
                v.RootGrid.Height = targetH;
            }

            v.FillPath.Fill = active ? activeBg : (v.IsHovered ? hoverBg : transparent);
            v.StrokePath.Stroke = active ? activeBorder : transparent;
            Brush fg = active ? activeFg : (v.IsHovered ? hoverFg : inactiveFg);
            v.Icon.Foreground = fg;
            v.TitleBlock.Foreground = fg;
            v.TitleBlock.FontWeight =
                active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            v.ContentPanel.Margin = active ? new Thickness(16, 0, 16, 0) : new Thickness(16, 0, 16, 2);
            Canvas.SetZIndex(v.RootGrid, active ? 5 : (v.IsHovered ? 2 : 1));
            if (heightChanged)
            {
                UpdateTabPathGeometries(v);
            }
        }

        foreach (var child in DisplayTabsPanel.Children)
        {
            if (child is Border sep && sep.Tag is ValueTuple<string, int> pair && pair.Item1 == "sep")
            {
                int leftIdx = pair.Item2;
                int pos = _renderedTabs.FindIndex(t => t.Index == leftIdx);
                bool nextIsActive = pos >= 0 && pos + 1 < _renderedTabs.Count &&
                                    _renderedTabs[pos + 1].Index == activeIdx;
                sep.Background = dividerBrush;
                sep.Visibility = (leftIdx == activeIdx || nextIsActive) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private void TryAdaptWindowSizeToHostAspect(int hostW, int hostH, bool recenter, bool markAdapted)
    {
        if (_isClosed || _appWindow == null || _isFullscreen || hostW < 64 || hostH < 64) return;

        if (_appWindow.Presenter is OverlappedPresenter op && op.State != OverlappedPresenterState.Restored)
        {
            _lastAutoSizedHostW = hostW;
            _lastAutoSizedHostH = hostH;
            if (markAdapted) _hasAdaptedToStreamAspect = true;
            return;
        }

        if (_lastAutoSizedHostW == hostW && _lastAutoSizedHostH == hostH && (!markAdapted || _hasAdaptedToStreamAspect))
        {
            return;
        }

        try
        {
            var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea == null) return;

            var workArea = displayArea.WorkArea;
            uint dpi = _hwnd != IntPtr.Zero ? GetDpiForWindow(_hwnd) : 96u;
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;

            double barDip = AppTitleBar.ActualHeight > 1 ? AppTitleBar.ActualHeight : 46.0;
            int titleBarPx = (int)Math.Round(barDip * scale);
            int minWpx = (int)Math.Ceiling((hostW < hostH ? 380.0 : 560.0) * scale);

            int maxClientWpx = Math.Max(minWpx, (int)Math.Floor(workArea.Width * 0.90));
            int maxVideoHpx = Math.Max(280,
                (int)Math.Floor(workArea.Height * 0.90) - titleBarPx - (int)Math.Round(16.0 * scale));

            double fitScale = Math.Min((double)maxClientWpx / hostW, (double)maxVideoHpx / hostH);
            double chosenScale = Math.Min(1.0, fitScale);

            int videoWpx = Math.Max(minWpx, (int)Math.Round(hostW * chosenScale));
            int videoHpx = Math.Max(240, (int)Math.Round((double)videoWpx * hostH / hostW));
            if (videoHpx > maxVideoHpx)
            {
                videoHpx = maxVideoHpx;
                videoWpx = Math.Max(minWpx, (int)Math.Round((double)videoHpx * hostW / hostH));
            }

            if (videoWpx > maxClientWpx)
            {
                videoWpx = maxClientWpx;
                videoHpx = Math.Max(240, (int)Math.Round((double)videoWpx * hostH / hostW));
            }

            _lastAutoSizedHostW = hostW;
            _lastAutoSizedHostH = hostH;
            if (markAdapted) _hasAdaptedToStreamAspect = true;

            int targetClientW = videoWpx;
            int targetClientH = videoHpx + titleBarPx;
            if (_hwnd != IntPtr.Zero &&
                GetWindowRect(_hwnd, out var wr) &&
                GetClientRect(_hwnd, out var cr) &&
                wr.Right > wr.Left && cr.Bottom > cr.Top)
            {
                int overheadW = Math.Max(0, (wr.Right - wr.Left) - (cr.Right - cr.Left));
                int overheadH = Math.Max(0, (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top));
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, targetClientW + overheadW, targetClientH + overheadH,
                    SwpNoMove | SwpNoZOrder | SwpNoActivate);
            }
            else
            {
                _appWindow.ResizeClient(new SizeInt32(targetClientW, targetClientH));
            }

            if (recenter)
            {
                WindowManagerHelper.CenterWindowOnScreen(_appWindow, targetClientW, targetClientH);
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                VerifyAndTrimVideoViewportAspect(hostW, hostH, recenter);
                UpdateVideoSwapChainSize();
                UpdateCachedVideoSurfaceBounds();
            });
        }
        catch
        {
        }
    }

    private void VerifyAndTrimVideoViewportAspect(int hostW, int hostH, bool recenter)
    {
        if (_isClosed || _isFullscreen || _hwnd == IntPtr.Zero || _appWindow == null || hostW <= 0 || hostH <= 0)
            return;
        if (_appWindow.Presenter is OverlappedPresenter op && op.State != OverlappedPresenterState.Restored)
            return;

        double dipW = VideoSwapChainPanel.ActualWidth;
        double dipH = VideoSwapChainPanel.ActualHeight;
        if (dipW < 64 || dipH < 64) return;

        float scale = VideoSwapChainPanel.CompositionScaleX > 0.1f
            ? VideoSwapChainPanel.CompositionScaleX
            : (float)(VideoSwapChainPanel.XamlRoot?.RasterizationScale ?? 1.0);
        int curWpx = (int)Math.Round(dipW * scale);
        int curHpx = (int)Math.Round(dipH * scale);
        int desiredHpx = Math.Max(200, (int)Math.Round((double)curWpx * hostH / hostW));
        int deltaH = desiredHpx - curHpx;
        if (Math.Abs(deltaH) < 1) return;

        if (GetWindowRect(_hwnd, out var wr))
        {
            int curWinW = wr.Right - wr.Left;
            int newWinH = Math.Max(320, (wr.Bottom - wr.Top) + deltaH);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, curWinW, newWinH, SwpNoMove | SwpNoZOrder | SwpNoActivate);
            if (recenter)
            {
                WindowManagerHelper.CenterWindowOnScreen(_appWindow, curWinW, newWinH);
            }

            UpdateVideoSwapChainSize();
            UpdateCachedVideoSurfaceBounds();
        }
    }

    private void InitializeOsdShadowLayer()
    {
        OsdShadowPanel.Children.Clear();
        var shadowBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE8, 0, 0, 0));
        foreach (UIElement child in OsdTextPanel.Children)
        {
            if (child is TextBlock src)
            {
                OsdShadowPanel.Children.Add(new TextBlock
                {
                    FontSize = src.FontSize,
                    FontWeight = src.FontWeight,
                    Foreground = shadowBrush,
                    Visibility = src.Visibility,
                    IsHitTestVisible = false
                });
            }
        }
    }

    private void SyncOsdShadow()
    {
        int count = Math.Min(OsdTextPanel.Children.Count, OsdShadowPanel.Children.Count);
        for (int i = 0; i < count; i++)
        {
            if (OsdTextPanel.Children[i] is TextBlock src && OsdShadowPanel.Children[i] is TextBlock dst)
            {
                dst.Text = src.Text;
                dst.Visibility = src.Visibility;
            }
        }
    }

    private void PopulateFpsCombo()
    {
        int[] options = [30, 60, 90, 120, 144, 165, 240];
        FpsCombo.Items.Clear();
        int sel = 1;
        for (int i = 0; i < options.Length; i++)
        {
            FpsCombo.Items.Add(new ComboBoxItem { Content = $"{options[i]} FPS", Tag = options[i] });
            if (options[i] == _currentFps) sel = i;
        }

        FpsCombo.SelectedIndex = sel;
    }

    private sealed record ScaleOption(string Label, int Percent, int TargetW, int TargetH);

    private void RebuildScaleOptions(int nativeW, int nativeH)
    {
        bool prev = _isUpdatingUi;
        _isUpdatingUi = true;
        try
        {
            nativeW = Math.Max(640, nativeW);
            nativeH = Math.Max(360, nativeH);
            _lastScaleNativeW = nativeW;
            _lastScaleNativeH = nativeH;
            HostNativeResInfoText.Text = LocalizationService.Format("Session_NativeResFmt", nativeW, nativeH);

            var opts = new List<ScaleOption>
            {
                new(LocalizationService.Format("Session_ScaleNativeFmt", nativeW, nativeH), 100, nativeW, nativeH)
            };

            int[] percents = [85, 75, 60, 50, 35];
            foreach (int pct in percents)
            {
                int sw = Math.Max(320, ((int)Math.Round(nativeW * pct / 100.0)) & ~1);
                int sh = Math.Max(180, ((int)Math.Round(nativeH * pct / 100.0)) & ~1);
                if (sw < nativeW && sh < nativeH)
                {
                    opts.Add(new(LocalizationService.Format("Session_ScalePercentFmt", pct, sw, sh), pct, sw, sh));
                }
            }

            (int rw, int rh)[] presets = [(2560, 1440), (1920, 1080), (1600, 900), (1280, 720)];
            foreach (var (rw, rh) in presets)
            {
                if (rw < nativeW && rh < nativeH)
                {
                    double boxScale = Math.Min((double)rw / nativeW, (double)rh / nativeH);
                    int fw = Math.Max(320, ((int)Math.Round(nativeW * boxScale)) & ~1);
                    int fh = Math.Max(180, ((int)Math.Round(nativeH * boxScale)) & ~1);
                    if (!opts.Any(o => o.TargetW == fw && o.TargetH == fh))
                    {
                        opts.Add(new(LocalizationService.Format("Session_ScaleBoxFmt", rw, rh, fw, fh), 0, fw, fh));
                    }
                }
            }

            ViewScaleCombo.Items.Clear();
            int selectedIdx = 0;
            for (int i = 0; i < opts.Count; i++)
            {
                ViewScaleCombo.Items.Add(new ComboBoxItem { Content = opts[i].Label, Tag = opts[i] });
                if (_targetCustomWidth > 0 && _targetCustomHeight > 0 &&
                    Math.Abs(opts[i].TargetW - _targetCustomWidth) <= 8 &&
                    Math.Abs(opts[i].TargetH - _targetCustomHeight) <= 8)
                {
                    selectedIdx = i;
                }
                else if (opts[i].Percent > 0 && opts[i].Percent == _targetScalePercent)
                {
                    selectedIdx = i;
                }
            }

            ViewScaleCombo.SelectedIndex = selectedIdx;
        }
        finally
        {
            _isUpdatingUi = prev;
        }
    }

    private void SendUpdatedVideoParams()
    {
        if (_clientHandle == IntPtr.Zero) return;

        int nativeW = _hostStatus.NativeWidth > 0 ? _hostStatus.NativeWidth : Math.Max(1920, _latestStats.NativeWidth);
        int nativeH = _hostStatus.NativeHeight > 0
            ? _hostStatus.NativeHeight
            : Math.Max(1080, _latestStats.NativeHeight);
        int reqW = _targetCustomWidth > 0 ? Math.Min(_targetCustomWidth, nativeW) : nativeW;
        int reqH = _targetCustomHeight > 0 ? Math.Min(_targetCustomHeight, nativeH) : nativeH;

        double pixelRatio = Math.Clamp((double)(reqW * (long)reqH) / Math.Max(1L, (long)nativeW * nativeH), 0.12, 1.0);
        int defaultAtRes = OsmiumSettings.GetDefaultBitrateKbps(reqW, reqH, _currentFps, _enableYuv444);
        int scaledMax = Math.Max(1500, (int)Math.Round(_baseBitrateKbps * pixelRatio * (_enableYuv444 ? 1.5 : 1.0)));
        int bitrate = Math.Clamp(Math.Min(scaledMax, defaultAtRes), 1500, 500000);

        NativeMethods.Osmium_ClientUpdateVideoParams(
            _clientHandle,
            reqW,
            reqH,
            _currentFps,
            bitrate,
            (_hostStatus.HostHdrEnabled && _enableHdr) ? 1 : 0,
            _enableYuv444 ? 1 : 0);
    }

    private void RefreshLocalMicList()
    {
        bool prev = _isUpdatingUi;
        _isUpdatingUi = true;
        try
        {
            var buf = new OsmiumAudioDeviceInfo[16];
            int count = NativeMethods.Osmium_EnumMicDevices(buf, buf.Length);
            _localMicDevices = buf.Take(count).ToList();

            LocalMicCombo.Items.Clear();
            int sel = -1;
            for (int i = 0; i < _localMicDevices.Count; i++)
            {
                var mic = _localMicDevices[i];
                LocalMicCombo.Items.Add(new ComboBoxItem
                {
                    Content = mic.IsDefault != 0
                        ? LocalizationService.Format("Session_MicDefaultFmt", mic.Name)
                        : mic.Name,
                    Tag = mic.Id
                });
                if (!string.IsNullOrEmpty(_selectedLocalMicId) && mic.Id == _selectedLocalMicId)
                {
                    sel = i;
                }
                else if (sel < 0 && mic.IsDefault != 0)
                {
                    sel = i;
                }
            }

            if (_localMicDevices.Count == 0)
            {
                LocalMicCombo.Items.Add(new ComboBoxItem
                    { Content = LocalizationService.GetString("Session_NoLocalMic"), Tag = string.Empty });
                LocalMicCombo.SelectedIndex = 0;
                VirtualMicToggle.IsEnabled = false;
            }
            else
            {
                VirtualMicToggle.IsEnabled = true;
                LocalMicCombo.SelectedIndex = Math.Max(0, sel);
                if (LocalMicCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string id)
                {
                    _selectedLocalMicId = id;
                }
            }
        }
        finally
        {
            _isUpdatingUi = prev;
        }
    }

    private bool IsVirtualMouseEngagedNow
        => _mouseMode == 1 || (_mouseMode == 2 && _hostStatus.SmartFullscreenEngaged);

    private bool ShouldCaptureCursorNow
        => _isWindowFocused &&
           !_isClosed &&
           !_remoteSessionEnded &&
           !_userPausedVirtualCapture &&
           _openFlyoutCount == 0 &&
           IsVirtualMouseEngagedNow;

    private void SyncKeyboardHookAndCursorCapture()
    {
        if (_isClosed) return;

        if (ShouldCaptureCursorNow)
        {
            EngageCursorCapture();
        }
        else
        {
            ReleaseCursorCapture(manualPause: false);
        }

        bool isForeground = IsSessionWindowForeground(out IntPtr fg);
        bool wantKeyHook = !_isClosed && (_isWindowFocused || _isCursorCaptured);
        bool wantHostForwarding = _isWindowFocused &&
                                  isForeground &&
                                  fg == _hwnd &&
                                  !_isClosed &&
                                  !_remoteSessionEnded &&
                                  _clientHandle != IntPtr.Zero &&
                                  _openFlyoutCount == 0 &&
                                  !IsTextInputFocused();
        SetLowLevelKeyboardHook(wantKeyHook);
        if (!wantHostForwarding)
        {
            ReleaseAllSwallowedRemoteKeys();
        }
    }

    private void OnCoordinatorClientSessionEnded(string reason)
    {
        if (_isClosed) return;
        DispatcherQueue.TryEnqueue(() => HandleRemoteSessionEnded(reason));
    }

    private static string BuildSessionEndedText(string? msg)
        => string.IsNullOrWhiteSpace(msg) || msg.Contains("Host ended", StringComparison.OrdinalIgnoreCase)
            ? LocalizationService.GetString("Session_EndedByHost")
            : LocalizationService.Format("Session_EndedFmt", msg);

    private void HandleRemoteSessionEnded(string? msg)
    {
        if (_isClosed || _remoteSessionEnded) return;
        _remoteSessionEnded = true;
        _endedReasonMsg = msg;

        _uiTimer.Stop();
        _gamepadTimer.Stop();
        _toastHideTimer.Stop();
        _fullscreenBarHideTimer.Stop();

        ReleaseCursorCapture(manualPause: false);
        SetLowLevelKeyboardHook(false);

        WaitingProgressRing.IsActive = false;
        WaitingProgressRing.Visibility = Visibility.Collapsed;
        WaitingVideoText.Text = BuildSessionEndedText(msg);
        WaitingVideoOverlay.Visibility = Visibility.Visible;

        StopMousePacer();
        IntPtr handle = Interlocked.Exchange(ref _clientHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            _ = Task.Run(() => NativeMethods.Osmium_StopClientEmbedded(handle));
        }
    }
}