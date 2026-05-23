using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using GameRes;
using GARbro.GUI.Strings;

namespace GARbro.GUI
{
    public partial class KrkrDumpAssistDialog : Window
    {
        readonly string m_source_archive;
        readonly KrkrDumpRunner m_runner = new KrkrDumpRunner();

        public ResourceParameterCommandResult Result { get; private set; }

        public KrkrDumpAssistDialog (string source_archive)
        {
            m_source_archive = source_archive;
            InitializeComponent();
            ApplyLocalization();
            SourceArchiveBox.Text = source_archive ?? "";
            GameExecutableBox.Text = SuggestGameExecutable (source_archive) ?? "";
            StatusText.Text = Text ("KrkrDumpInitialStatus");
        }

        void ApplyLocalization ()
        {
            Title = Text ("KrkrDumpTitle");
            SourceArchiveLabel.Text = Text ("KrkrDumpSourceArchive");
            GameExecutableLabel.Text = Text ("KrkrDumpGameExecutable");
            BrowseExeButton.Content = Text ("KrkrDumpBrowse");
            RunButton.Content = Text ("KrkrDumpStart");
            CancelButton.Content = guiStrings.ButtonCancel;
        }

        void BrowseExe_Click (object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog {
                CheckFileExists = true,
                CheckPathExists = true,
                Filter = Text ("KrkrDumpExecutableFilter"),
                Multiselect = false,
                Title = Text ("KrkrDumpSelectExecutable"),
            };
            if (!string.IsNullOrEmpty (GameExecutableBox.Text))
                dlg.InitialDirectory = Path.GetDirectoryName (GameExecutableBox.Text);
            else if (!string.IsNullOrEmpty (m_source_archive))
                dlg.InitialDirectory = Path.GetDirectoryName (m_source_archive);

            if (dlg.ShowDialog (this).Value)
            {
                GameExecutableBox.Text = dlg.FileName;
            }
        }

        async void RunButton_Click (object sender, RoutedEventArgs e)
        {
            KrkrDumpRunRequest request;
            try
            {
                request = CreateRequest();
            }
            catch (Exception X)
            {
                StatusText.Text = X.Message;
                return;
            }

            SetRunning (true);
            StatusText.Text = Text ("KrkrDumpStarting");
            try
            {
                Result = await Task.Run (() => m_runner.Run (request, SetStatus));
                DialogResult = true;
            }
            catch (OperationCanceledException X)
            {
                StatusText.Text = X.Message;
            }
            catch (Exception X)
            {
                StatusText.Text = X.Message;
                MessageBox.Show (this, X.Message, Text ("KrkrDumpCaption"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (DialogResult != true)
                    SetRunning (false);
            }
        }

        KrkrDumpRunRequest CreateRequest ()
        {
            if (string.IsNullOrEmpty (m_source_archive) || !File.Exists (m_source_archive))
                throw new FileNotFoundException (Text ("KrkrDumpArchiveNotFound"), m_source_archive);
            if (string.IsNullOrEmpty (GameExecutableBox.Text) || !File.Exists (GameExecutableBox.Text))
                throw new FileNotFoundException (Text ("KrkrDumpExecutableNotFound"), GameExecutableBox.Text);

            return new KrkrDumpRunRequest
            {
                SourceArchive = m_source_archive,
                GameExecutable = GameExecutableBox.Text,
                OutputDirectory = CreateDefaultOutputDirectory (m_source_archive, GameExecutableBox.Text),
            };
        }

        void SetRunning (bool is_running)
        {
            GameExecutableBox.IsEnabled = !is_running;
            BrowseExeButton.IsEnabled = !is_running;
            RunButton.IsEnabled = !is_running;
            CancelButton.IsEnabled = !is_running;
        }

        void SetStatus (string status)
        {
            Dispatcher.BeginInvoke (new Action (() => StatusText.Text = status));
        }

        static string SuggestGameExecutable (string archive)
        {
            if (string.IsNullOrEmpty (archive))
                return null;
            var dir = Path.GetDirectoryName (archive);
            if (string.IsNullOrEmpty (dir) || !Directory.Exists (dir))
                return null;

            return Directory.GetFiles (dir, "*.exe")
                .Where (x => !IsAuxiliaryExecutable (Path.GetFileNameWithoutExtension (x)))
                .OrderByDescending (x => new FileInfo (x).Length)
                .FirstOrDefault();
        }

        static bool IsAuxiliaryExecutable (string name)
        {
            if (string.IsNullOrEmpty (name))
                return true;
            name = name.ToLowerInvariant();
            return name.Contains ("config") || name.Contains ("setting") || name.Contains ("setup")
                || name.Contains ("uninst") || name.Contains ("update") || name.Contains ("patch");
        }

        static string CreateDefaultOutputDirectory (string archive, string exe)
        {
            var app = Application.Current as App;
            string root = null != app ? app.GetLocalAppDataFolder() : Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData);
            string exe_name = string.IsNullOrEmpty (exe) ? "game" : Path.GetFileNameWithoutExtension (exe);
            string arc_name = string.IsNullOrEmpty (archive) ? "xp3" : Path.GetFileNameWithoutExtension (archive);
            string dir_name = string.Format ("{0}_{1}_{2:yyyyMMdd_HHmmss}", SanitizeFileName (exe_name),
                                             SanitizeFileName (arc_name), DateTime.Now);
            return Path.Combine (root, "KrkrDump", dir_name);
        }

        static string SanitizeFileName (string name)
        {
            if (string.IsNullOrEmpty (name))
                return "item";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace (c, '_');
            return name;
        }

        static string Text (string name)
        {
            return guiStrings.ResourceManager.GetString (name) ?? name;
        }
    }
}
