//! \file       CodexSkillPackage.cs
//! \date       2026 Jul 27
//! \brief      Exports the bundled GARbro CLI skill ZIP package.

using System;
using System.IO;

namespace GARbro.GUI
{
    internal static class CodexSkillPackage
    {
        internal const string PackageFileName = "garbro-cli-skill.zip";

        internal static string PackagePath {
            get {
                return Path.Combine (
                    AppDomain.CurrentDomain.BaseDirectory, PackageFileName);
            }
        }

        internal static bool IsAvailable {
            get {
                try
                {
                    return File.Exists (PackagePath)
                        && new FileInfo (PackagePath).Length > 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static string DefaultSaveDirectory {
            get {
                var userProfile = Environment.GetFolderPath (
                    Environment.SpecialFolder.UserProfile);
                var downloads = Path.Combine (userProfile, "Downloads");
                if (Directory.Exists (downloads))
                    return downloads;
                return Environment.GetFolderPath (
                    Environment.SpecialFolder.MyDocuments);
            }
        }

        internal static void SaveTo (string destinationPath)
        {
            SaveTo (destinationPath, PackagePath);
        }

        internal static void SaveTo (
            string destinationPath, string packagePath)
        {
            if (string.IsNullOrWhiteSpace (packagePath)
                || !File.Exists (packagePath)
                || new FileInfo (packagePath).Length == 0)
            {
                throw new FileNotFoundException (
                    "The bundled GARbro CLI skill ZIP package is missing.",
                    packagePath);
            }
            if (string.IsNullOrWhiteSpace (destinationPath))
                throw new ArgumentException (
                    "A destination ZIP path is required.",
                    "destinationPath");

            var sourcePath = Path.GetFullPath (packagePath);
            destinationPath = Path.GetFullPath (destinationPath);
            if (string.Equals (
                    sourcePath, destinationPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var destinationDirectory = Path.GetDirectoryName (destinationPath);
            if (string.IsNullOrEmpty (destinationDirectory)
                || !Directory.Exists (destinationDirectory))
            {
                throw new DirectoryNotFoundException (
                    "The selected destination directory does not exist.");
            }

            var temporaryPath = Path.Combine (
                destinationDirectory,
                "." + Path.GetFileName (destinationPath)
                + ".partial-" + Guid.NewGuid().ToString ("N"));
            try
            {
                File.Copy (sourcePath, temporaryPath);
                if (File.Exists (destinationPath))
                    File.Replace (temporaryPath, destinationPath, null);
                else
                    File.Move (temporaryPath, destinationPath);
            }
            finally
            {
                if (File.Exists (temporaryPath))
                    File.Delete (temporaryPath);
            }
        }
    }
}
