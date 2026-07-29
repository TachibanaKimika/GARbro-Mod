using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using GameRes;
using GameRes.Formats.KiriKiri;
using GameRes.Formats.Strings;

namespace GameRes.Formats.GUI
{
    /// <summary>
    /// Interaction logic for WidgetXP3.xaml
    /// </summary>
    public partial class WidgetXP3 : StackPanel, IResourceParameterContextReceiver, IResourceParameterCommandSource,
                                      IResourceToolResultConsumer
    {
        const string KrkrDumpCommandName = "KiriKiri.KrkrDump";
        const string HxNamesCommandName = "KiriKiri.HxNamesImport";

        string m_source_file;
        string m_imported_scheme;
        bool m_resetting_scheme;
        Action<ResourceProgressInfo> m_progress_reporter;

        public event EventHandler<ResourceParameterCommandEventArgs> ParameterCommandRequested;

        public WidgetXP3 ()
        {
            var last_selected = Properties.Settings.Default.XP3Scheme;
            if (Xp3Opener.IsTransientSchemeName (last_selected))
                last_selected = null;
            InitializeComponent();
            KrkrDumpButton.Content = Text ("KrkrDumpButton");
            HxNamesPresetLabel.Text = Text ("HxNamesPresetLabel");
            HxNamesPreset.ItemsSource = new[] {
                new HxNamesPresetItem (null, Text ("HxNamesPresetAutomatic")),
                new HxNamesPresetItem (
                    KrkrDumpResultImporter.LimelightLemonadeJamPresetId,
                    Text ("HxNamesPresetLLLJ")),
            };
            HxNamesPreset.SelectedIndex = 0;
            HxNamesPresetButton.Content = Text ("HxNamesPresetButton");
            HxNamesButton.Content = Text ("HxNamesButton");
            HxNamesSameDirectoryText.Text = Text ("HxNamesSameDirectory");
            ResetSchemeSource();
            this.Loaded += (s, e) => {
                m_resetting_scheme = true;
                try
                {
                    if (!string.IsNullOrEmpty (last_selected))
                        this.Scheme.SelectedValue = last_selected;
                    else
                        this.Scheme.SelectedIndex = 0;
                }
                finally
                {
                    m_resetting_scheme = false;
                }
            };
        }

        public ICrypt GetScheme ()
        {
            if (!string.IsNullOrEmpty (m_imported_scheme))
                return Xp3Opener.GetScheme (m_imported_scheme);

            var selected = Scheme.SelectedValue as string;
            if (!string.IsNullOrEmpty (selected) && !Xp3Opener.IsTransientSchemeName (selected))
                Properties.Settings.Default.XP3Scheme = selected;
            return Xp3Opener.GetScheme (selected);
        }

        public void SetResourceContext (ResourceParameterContext context)
        {
            m_progress_reporter = null != context ? context.ProgressReporter : null;
            if (null != context)
            {
                m_source_file = context.SourceFileName;
                m_imported_scheme = Xp3Opener.IsTransientSchemeFor (m_source_file)
                    ? Xp3Opener.TransientSchemeName
                    : null;
            }
        }

        void ResetSchemeSource ()
        {
            m_resetting_scheme = true;
            try
            {
                var selected = Scheme != null ? Scheme.SelectedValue as string : null;
                if (Xp3Opener.IsTransientSchemeName (selected))
                    selected = null;
                if (string.IsNullOrEmpty (selected))
                    selected = Properties.Settings.Default.XP3Scheme;
                if (Xp3Opener.IsTransientSchemeName (selected))
                    selected = null;
                var keys = new[] { new SchemeItem (arcStrings.ArcNoEncryption, arcStrings.ArcNoEncryption, Xp3Opener.NoCryptAlgorithm) };
                var schemes = Xp3Opener.KnownSchemes
                    .Where (x => !Xp3Opener.IsTransientSchemeName (x.Key))
                    .OrderBy (x => Xp3Opener.GetSchemeDisplayName (x.Key))
                    .ThenBy (x => x.Key)
                    .Select (x => new SchemeItem (x.Key, Xp3Opener.GetSchemeDisplayName (x.Key), x.Value));
                this.DataContext = keys.Concat (schemes);
                if (!string.IsNullOrEmpty (selected))
                    Scheme.SelectedValue = selected;
            }
            finally
            {
                m_resetting_scheme = false;
            }
        }

        void OnSchemeSelectionChanged (object sender, SelectionChangedEventArgs e)
        {
            if (!m_resetting_scheme)
                m_imported_scheme = null;
        }

        async void OnKrkrDumpClick (object sender, System.Windows.RoutedEventArgs e)
        {
            string preset_file;
            string preset_name;
            string preset_error;
            if (!TryResolveSelectedPreset (false, null, out preset_file, out preset_name, out preset_error))
            {
                KrkrDumpStatus.Text = preset_error;
                return;
            }
            var handler = ParameterCommandRequested;
            if (null == handler)
            {
                KrkrDumpStatus.Text = Text ("KrkrDumpUnavailable");
                return;
            }
            KrkrDumpStatus.Text = Text ("KrkrDumpStarting");
            var args = new ResourceParameterCommandEventArgs (KrkrDumpCommandName) { SourceFileName = m_source_file };
            handler (this, args);
            if (!args.Handled)
            {
                KrkrDumpStatus.Text = Text ("KrkrDumpRequestUnhandled");
                return;
            }
            KrkrDumpStatus.Text = Text ("HxNamesGenerating");
            KrkrDumpButton.IsEnabled = false;
            HxNamesPreset.IsEnabled = false;
            HxNamesPresetButton.IsEnabled = false;
            HxNamesButton.IsEnabled = false;
            var progress_reporter = m_progress_reporter;
            ReportProgress (progress_reporter, 0, Text ("HxNamesGenerating"));
            try
            {
                var result = args.Result;
                AttachPreset (result, preset_file);
                var source_file = m_source_file;
                var same_directory = HxNamesSameDirectory.IsChecked == true;
                var import = await Task.Run (() => KrkrDumpResultImporter.Import (
                    result, source_file, same_directory, progress_reporter));
                DescribeAppliedPreset (import, preset_name);
                KrkrDumpStatus.Text = import.Message;
                ApplyImportResult (import);
                ReportProgress (progress_reporter, 100, import.Message, true);
            }
            catch (Exception X)
            {
                KrkrDumpStatus.Text = X.Message;
                ReportProgress (progress_reporter, 100, X.Message, true);
            }
            finally
            {
                KrkrDumpButton.IsEnabled = true;
                HxNamesPreset.IsEnabled = true;
                HxNamesPresetButton.IsEnabled = true;
                HxNamesButton.IsEnabled = true;
            }
        }

        void OnHxNamesPresetClick (object sender, System.Windows.RoutedEventArgs e)
        {
            var base_scheme = GetHxNamesBaseScheme();
            if (!(Xp3Opener.GetScheme (base_scheme) is HxCrypt))
            {
                KrkrDumpStatus.Text = Text ("HxNamesNeedHxScheme");
                return;
            }

            string preset_file;
            string preset_name;
            string preset_error;
            if (!TryResolveSelectedPreset (true, null, out preset_file, out preset_name, out preset_error))
            {
                KrkrDumpStatus.Text = preset_error;
                return;
            }

            var result = new ResourceParameterCommandResult { Success = true };
            result.Metadata["NamesFile"] = preset_file;
            var import = KrkrDumpResultImporter.ImportNamesFile (
                result, m_source_file, base_scheme, HxNamesSameDirectory.IsChecked == true);
            DescribeAppliedPreset (import, preset_name);
            KrkrDumpStatus.Text = import.Message;
            ApplyImportResult (import);
        }

        void OnHxNamesClick (object sender, System.Windows.RoutedEventArgs e)
        {
            var base_scheme = GetHxNamesBaseScheme();
            if (!(Xp3Opener.GetScheme (base_scheme) is HxCrypt))
            {
                KrkrDumpStatus.Text = Text ("HxNamesNeedHxScheme");
                return;
            }
            var handler = ParameterCommandRequested;
            if (null == handler)
            {
                KrkrDumpStatus.Text = Text ("HxNamesUnavailable");
                return;
            }
            KrkrDumpStatus.Text = Text ("HxNamesSelecting");
            var args = new ResourceParameterCommandEventArgs (HxNamesCommandName) { SourceFileName = m_source_file };
            handler (this, args);
            if (!args.Handled)
            {
                KrkrDumpStatus.Text = Text ("HxNamesRequestUnhandled");
                return;
            }
            var import = KrkrDumpResultImporter.ImportNamesFile (
                args.Result, m_source_file, base_scheme, HxNamesSameDirectory.IsChecked == true);
            KrkrDumpStatus.Text = import.Message;
            ApplyImportResult (import);
        }

        public bool ApplyResourceToolResult (ResourceParameterCommandResult result, out string message)
        {
            string preset_file;
            string preset_name;
            string preset_error;
            if (!TryResolveSelectedPreset (false, result, out preset_file, out preset_name, out preset_error))
            {
                message = preset_error;
                return false;
            }
            AttachPreset (result, preset_file);
            var progress_reporter = m_progress_reporter;
            ReportProgress (progress_reporter, 0, Text ("HxNamesGenerating"));
            try
            {
                var import = KrkrDumpResultImporter.Import (
                    result, m_source_file, HxNamesSameDirectory.IsChecked == true, progress_reporter);
                DescribeAppliedPreset (import, preset_name);
                message = import.Message;
                ReportProgress (progress_reporter, 100, import.Message, true);
                return ApplyImportResult (import);
            }
            catch (Exception X)
            {
                ReportProgress (progress_reporter, 100, X.Message, true);
                throw;
            }
        }

        string GetHxNamesBaseScheme ()
        {
            return !string.IsNullOrEmpty (m_imported_scheme)
                ? m_imported_scheme
                : Scheme.SelectedValue as string;
        }

        bool TryResolveSelectedPreset (bool resolve_automatic,
                                       ResourceParameterCommandResult result,
                                       out string preset_file,
                                       out string preset_name,
                                       out string error)
        {
            preset_file = null;
            preset_name = null;
            error = null;
            var selected = HxNamesPreset.SelectedItem as HxNamesPresetItem;
            var preset_id = null != selected ? selected.Key : null;
            if (string.IsNullOrEmpty (preset_id) && !resolve_automatic)
                return true;

            preset_file = KrkrDumpResultImporter.ResolveInstalledNamesPresetFile (
                preset_id, result, m_source_file);
            if (string.IsNullOrEmpty (preset_file))
            {
                error = Text ("HxNamesPresetNotDetected");
                return false;
            }
            if (!System.IO.File.Exists (preset_file))
            {
                error = string.Format (Text ("HxNamesPresetFileNotFound"), preset_file);
                return false;
            }
            if (null != selected && !string.IsNullOrEmpty (selected.Key))
            {
                preset_name = selected.Name;
            }
            else
            {
                var lllj_file = KrkrDumpResultImporter.ResolveInstalledNamesPresetFile (
                    KrkrDumpResultImporter.LimelightLemonadeJamPresetId, result, m_source_file);
                preset_name = string.Equals (
                    preset_file, lllj_file, StringComparison.OrdinalIgnoreCase)
                    ? Text ("HxNamesPresetLLLJ")
                    : System.IO.Path.GetFileName (preset_file);
            }
            return true;
        }

        static void AttachPreset (ResourceParameterCommandResult result, string preset_file)
        {
            if (null != result && !string.IsNullOrEmpty (preset_file))
                result.Metadata["HxNamesFile"] = preset_file;
        }

        static void DescribeAppliedPreset (KrkrDumpImportResult import, string preset_name)
        {
            if (null != import && import.Success && !string.IsNullOrEmpty (preset_name))
            {
                import.Message = string.Format (
                    Text ("HxNamesPresetApplied"), preset_name, import.Message);
            }
        }

        bool ApplyImportResult (KrkrDumpImportResult import)
        {
            if (null == import || !import.Success)
                return false;
            ResetSchemeSource();
            m_imported_scheme = import.SchemeName;
            return true;
        }

        static string Text (string name)
        {
            return arcStrings.ResourceManager.GetString (name) ?? name;
        }

        static void ReportProgress (Action<ResourceProgressInfo> reporter, int percentage,
                                    string message, bool completed = false)
        {
            if (null == reporter)
                return;
            try
            {
                reporter (new ResourceProgressInfo {
                    Percentage = percentage,
                    Message = message,
                    IsCompleted = completed,
                });
            }
            catch (Exception X)
            {
                System.Diagnostics.Trace.WriteLine (
                    "HxNames progress reporter failed: " + X.Message, "[HxNames]");
            }
        }
    }

    internal class HxNamesPresetItem
    {
        public string Key { get; private set; }
        public string Name { get; private set; }

        public HxNamesPresetItem (string key, string name)
        {
            Key = key;
            Name = name;
        }
    }

    internal class SchemeItem
    {
        public string Key { get; private set; }
        public string Name { get; private set; }
        public ICrypt Value { get; private set; }

        public SchemeItem (string key, string name, ICrypt value)
        {
            Key = key;
            Name = string.IsNullOrWhiteSpace (name) ? key : name;
            Value = value;
        }
    }

    internal class ClassNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value != null)
                return value.GetType().Name;
            else
                return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
