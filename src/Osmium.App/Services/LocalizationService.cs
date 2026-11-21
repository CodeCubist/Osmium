using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Osmium.App.Services;

public static class LocalizationService
{
    private static readonly string SystemDefaultLanguage =
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";

    private static ResourceManager? _manager;
    private static ResourceContext? _context;
    private static string _activeLanguage = "zh-CN";

    public static event Action? LanguageChanged;

    public static string ActiveLanguage => _activeLanguage;

    public static string ResolveLanguage(string? preferredLanguage) => preferredLanguage switch
    {
        "en-US" => "en-US",
        "zh-CN" => "zh-CN",
        _ => SystemDefaultLanguage
    };

    public static void Initialize(string preferredLanguage)
    {
        try
        {
            _manager ??= new ResourceManager();
            _context ??= _manager.CreateResourceContext();
        }
        catch
        {
            _manager = null;
            _context = null;
        }

        string resolved = ResolveLanguage(preferredLanguage);
        _activeLanguage = resolved;
        if (_context != null)
        {
            try
            {
                _context.QualifierValues["Language"] = resolved;
            }
            catch
            {
            }
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(resolved);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch
        {
        }
    }

    public static void ApplyLanguage(string preferredLanguage)
    {
        string resolved = ResolveLanguage(preferredLanguage);
        bool changed = !string.Equals(_activeLanguage, resolved, StringComparison.OrdinalIgnoreCase);
        _activeLanguage = resolved;
        if (_context != null)
        {
            try
            {
                _context.QualifierValues["Language"] = resolved;
            }
            catch
            {
            }
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(resolved);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch
        {
        }

        if (changed)
        {
            LanguageChanged?.Invoke();
        }
    }

    public static string GetString(string key)
    {
        try
        {
            if (_manager == null || _context == null)
            {
                Initialize(_activeLanguage);
            }

            if (_manager == null || _context == null)
            {
                return key;
            }

            var candidate = _manager.MainResourceMap.TryGetValue($"Resources/{key}", _context);
            if (candidate != null)
            {
                return candidate.ValueAsString;
            }
        }
        catch
        {
        }

        return key;
    }

    public static string Format(string key, params object[] args)
    {
        string template = GetString(key);
        try
        {
            return string.Format(CultureInfo.CurrentUICulture, template, args);
        }
        catch
        {
            return template;
        }
    }

    public static void SetComboItems(ComboBox combo, params (string resKey, string tag)[] items)
    {
        string? selectedTag = (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        combo.Items.Clear();
        int matchIdx = 0;
        for (int i = 0; i < items.Length; i++)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = GetString(items[i].resKey),
                Tag = items[i].tag
            });
            if (items[i].tag == selectedTag)
            {
                matchIdx = i;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = matchIdx;
        }
    }
}