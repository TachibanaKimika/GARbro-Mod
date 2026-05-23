using System;
using System.Collections.Generic;
using System.Linq;
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

        string m_source_file;
        string m_imported_scheme;
        bool m_resetting_scheme;

        public event EventHandler<ResourceParameterCommandEventArgs> ParameterCommandRequested;

        public WidgetXP3 ()
        {
            var last_selected = Properties.Settings.Default.XP3Scheme;
            if (Xp3Opener.IsTransientSchemeName (last_selected))
                last_selected = null;
            InitializeComponent();
            KrkrDumpButton.Content = Text ("KrkrDumpButton");
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
            if (null != context)
            {
                m_source_file = context.SourceFileName;
                if (Xp3Opener.IsTransientSchemeFor (m_source_file))
                    m_imported_scheme = Xp3Opener.TransientSchemeName;
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

        void OnKrkrDumpClick (object sender, System.Windows.RoutedEventArgs e)
        {
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
            string message;
            ApplyResourceToolResult (args.Result, out message);
            KrkrDumpStatus.Text = message;
        }

        public bool ApplyResourceToolResult (ResourceParameterCommandResult result, out string message)
        {
            var import = KrkrDumpResultImporter.Import (result, m_source_file);
            message = import.Message;
            if (!import.Success)
                return false;

            ResetSchemeSource();
            m_imported_scheme = import.SchemeName;
            return true;
        }

        static string Text (string name)
        {
            return arcStrings.ResourceManager.GetString (name) ?? name;
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
