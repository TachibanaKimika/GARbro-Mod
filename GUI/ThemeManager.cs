using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using GARbro.GUI.Properties;

namespace GARbro.GUI
{
    internal static class ThemeManager
    {
        public const string SystemPreference = "System";
        public const string LightTheme = "Light";
        public const string DarkTheme = "Dark";

        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const string AppsUseLightThemeValue = "AppsUseLightTheme";

        static bool m_initialized;
        static ResourceDictionary m_active_theme_dictionary;

        public static string EffectiveTheme { get; private set; }

        public static event EventHandler ThemeChanged;

        public static void Initialize ()
        {
            if (m_initialized)
                return;
            m_initialized = true;
            EventManager.RegisterClassHandler (typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler (OnWindowLoaded));
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            ApplyCurrentPreference();
        }

        public static void Shutdown ()
        {
            if (!m_initialized)
                return;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            m_initialized = false;
        }

        public static string NormalizePreference (string preference)
        {
            if (string.Equals (preference, LightTheme, StringComparison.OrdinalIgnoreCase))
                return LightTheme;
            if (string.Equals (preference, DarkTheme, StringComparison.OrdinalIgnoreCase))
                return DarkTheme;
            return SystemPreference;
        }

        public static void ApplyCurrentPreference ()
        {
            ApplyPreference (Settings.Default.appTheme);
        }

        public static void ApplyPreference (string preference)
        {
            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke ((Action)(() => ApplyPreference (preference)));
                return;
            }
            ApplyPreferenceCore (preference);
        }

        static void ApplyPreferenceCore (string preference)
        {
            string effective_theme = ResolveEffectiveTheme (preference);
            if (m_active_theme_dictionary == null || !string.Equals (EffectiveTheme, effective_theme, StringComparison.Ordinal))
            {
                ReplaceThemeDictionary (effective_theme);
                EffectiveTheme = effective_theme;
                ApplyWindowThemes();
                OnThemeChanged();
            }
        }

        static string ResolveEffectiveTheme (string preference)
        {
            preference = NormalizePreference (preference);
            if (preference == LightTheme || preference == DarkTheme)
                return preference;
            return IsSystemAppThemeDark() ? DarkTheme : LightTheme;
        }

        static bool IsSystemAppThemeDark ()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey (PersonalizeKey))
                {
                    object value = key != null ? key.GetValue (AppsUseLightThemeValue) : null;
                    if (value is int)
                        return 0 == (int)value;
                    if (value is string)
                    {
                        int int_value;
                        if (int.TryParse ((string)value, out int_value))
                            return 0 == int_value;
                    }
                }
            }
            catch (Exception X)
            {
                Trace.WriteLine ("System theme detection failed: " + X.Message, "[ThemeManager]");
            }
            return false;
        }

        static void ReplaceThemeDictionary (string effective_theme)
        {
            var resources = Application.Current.Resources.MergedDictionaries;
            for (int i = resources.Count - 1; i >= 0; --i)
            {
                if (IsThemeDictionary (resources[i]))
                    resources.RemoveAt (i);
            }

            m_active_theme_dictionary = new ResourceDictionary {
                Source = new Uri ("Themes/Theme." + effective_theme + ".xaml", UriKind.Relative)
            };
            resources.Add (m_active_theme_dictionary);
        }

        static bool IsThemeDictionary (ResourceDictionary dictionary)
        {
            if (dictionary == null || dictionary.Source == null)
                return false;
            string source = dictionary.Source.OriginalString.Replace ('\\', '/');
            return source.EndsWith ("Themes/Theme.Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith ("Themes/Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase);
        }

        static void OnUserPreferenceChanged (object sender, UserPreferenceChangedEventArgs e)
        {
            if (NormalizePreference (Settings.Default.appTheme) == SystemPreference)
                ApplyCurrentPreference();
        }

        static void OnWindowLoaded (object sender, RoutedEventArgs e)
        {
            RegisterWindow (sender as Window);
        }

        public static void RegisterWindow (Window window)
        {
            if (window == null)
                return;
            window.SourceInitialized -= OnWindowSourceInitialized;
            window.SourceInitialized += OnWindowSourceInitialized;
            ApplyWindowTheme (window);
        }

        static void OnWindowSourceInitialized (object sender, EventArgs e)
        {
            ApplyWindowTheme (sender as Window);
        }

        static void ApplyWindowThemes ()
        {
            var app = Application.Current;
            if (app == null)
                return;
            foreach (Window window in app.Windows)
                RegisterWindow (window);
        }

        static void ApplyWindowTheme (Window window)
        {
            if (window == null)
                return;
            IntPtr hwnd = new WindowInteropHelper (window).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            ApplyDarkTitleBar (hwnd, EffectiveTheme == DarkTheme);
        }

        static void ApplyDarkTitleBar (IntPtr hwnd, bool enabled)
        {
            try
            {
                int value = enabled ? 1 : 0;
                int result = DwmSetWindowAttribute (hwnd, 20, ref value, Marshal.SizeOf (typeof(int)));
                if (result != 0)
                    DwmSetWindowAttribute (hwnd, 19, ref value, Marshal.SizeOf (typeof(int)));
            }
            catch (Exception X)
            {
                Trace.WriteLine ("Dark title bar update failed: " + X.Message, "[ThemeManager]");
            }
        }

        static void OnThemeChanged ()
        {
            var handler = ThemeChanged;
            if (handler != null)
                handler (null, EventArgs.Empty);
        }

        [DllImport ("dwmapi.dll")]
        static extern int DwmSetWindowAttribute (IntPtr hwnd, int attribute, ref int attribute_value, int attribute_size);
    }
}
