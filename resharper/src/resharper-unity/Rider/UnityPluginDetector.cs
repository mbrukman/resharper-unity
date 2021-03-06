﻿#if RIDER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using JetBrains.ProjectModel;
using JetBrains.Util;

namespace JetBrains.ReSharper.Plugins.Unity.Rider
{
    [SolutionComponent]
    public class UnityPluginDetector
    {
        public static readonly Version ZeroVersion = new Version();
        
        private readonly ISolution mySolution;
        private readonly ILogger myLogger;
        private static readonly string[] ourPluginFilesV180 = {"RiderAssetPostprocessor.cs", "RiderPlugin.cs"};

        public static readonly string MergedPluginFile = "Unity3DRider.cs";

        private static readonly Regex ourVersionRegex = new Regex(@"//\s+(?:Version:)?\s*((?:[0-9]+\.)+[0-9]+)", RegexOptions.Compiled);

        public static readonly InstallationInfo ShouldNotInstall = new InstallationInfo(false, FileSystemPath.Empty,
            EmptyArray<FileSystemPath>.Instance, ZeroVersion);

        public UnityPluginDetector(ISolution solution, ILogger logger)
        {
            mySolution = solution;
            myLogger = logger;
        }

        [NotNull]
        public InstallationInfo GetInstallationInfo(ICollection<IProject> unityProjects, FileSystemPath previousInstallationDir)
        {
            try
            {
                var solutionDir = mySolution.SolutionFilePath.Directory;
                if (solutionDir.IsNullOrEmpty())
                {
                    myLogger.Warn("Solution dir is null or empty. Skipping installation.");
                    return ShouldNotInstall;
                }

                if (!solutionDir.IsAbsolute)
                {
                    myLogger.Warn("Solution dir is not absolute. Skipping installation.");
                }
                
                var assetsDir = solutionDir.CombineWithShortName("Assets");
                if (!assetsDir.ExistsDirectory)
                {
                    myLogger.Info("No Assets directory in the same directory as solution. Skipping installation.");
                    return ShouldNotInstall;
                }
                
                var defaultDir = assetsDir
                    .CombineWithShortName("Plugins")
                    .CombineWithShortName("Editor")
                    .CombineWithShortName("JetBrains");

                InstallationInfo result;
                
                var isFirstInstall = previousInstallationDir.IsNullOrEmpty();
                if (isFirstInstall)
                {
                    // e.g.: fresh checkout from VCS
                    if (TryFindInSolution(unityProjects, out result))
                    {
                        return result;
                    }
                    
                    if (TryFindOnDisk(defaultDir, out result))
                    {
                        return result;
                    }

                    // nothing in solution or default directory on first launch.
                    return NotInstalled(defaultDir);
                }

                // default case: all is good, we have cached the installation dir
                if (TryFindOnDisk(previousInstallationDir, out result))
                {
                    return result;
                }
                
                // e.g.: user has moved the plugin from the time it was last installed
                // In such case we will be able to find if solution was regenerated by Unity after that
                if (TryFindInSolution(unityProjects, out result))
                {
                    return result;
                }
                
                // not fresh install, but nothing in previously installed dir on in solution
                myLogger.Info("Plugin not found in previous installation dir '{0}' or in solution. Falling back to default directory.", previousInstallationDir);
                
                return NotInstalled(defaultDir);
            }
            catch (Exception e)
            {
                myLogger.LogExceptionSilently(e);
                return ShouldNotInstall;
            }
        }

        private bool TryFindInSolution(ICollection<IProject> unityProjects, [NotNull] out InstallationInfo result)
        {
            myLogger.Verbose("Looking for plugin in solution.");
            foreach (var project in unityProjects)
            {
                if (TryFindInProject(project, out result))
                {
                    return true;
                }
            }

            result = ShouldNotInstall;
            return false;
        }

        private bool TryFindInProject(IProject project, [NotNull] out InstallationInfo result)
        {
            if (!(project.ProjectFileLocation?.Directory.CombineWithShortName("Assets").ExistsDirectory).GetValueOrDefault())
            {
                result = ShouldNotInstall;
                return false;
            }
            
            var pluginFiles = project
                .GetAllProjectFiles(f =>
                {
                    var location = f.Location;
                    if (location == null) return false;

                    var fileName = location.Name;
                    return ourPluginFilesV180.Contains(fileName) || fileName == MergedPluginFile;
                })
                .Select(f => f.Location)
                .ToList();

            var isMoved = pluginFiles.Any(f => !f.ExistsFile);
            if (isMoved)
            {
                myLogger.Verbose("Plugin was moved and solution was not updated. Will wait until user reopens updated solution.");
                
                result = ShouldNotInstall;
                return true;
            }

            if (pluginFiles.Count == 0)
            {
                result = ShouldNotInstall;
                return false;
            }

            result = ExistingInstallation(pluginFiles);
            return true;
        }
        
        private bool TryFindOnDisk(FileSystemPath directory, [NotNull] out InstallationInfo result)
        {
            myLogger.Verbose("Looking for plugin on disk: '{0}'", directory);
            var pluginFiles = directory
                .GetChildFiles("*.cs")
                .Where(f => ourPluginFilesV180.Contains(f.Name) || f.Name == MergedPluginFile)
                .ToList();

            if (pluginFiles.Count == 0)
            {
                result = ShouldNotInstall;
                return false;
            }

            result = ExistingInstallation(pluginFiles);
            return true;
        }

        [NotNull]
        private static InstallationInfo NotInstalled(FileSystemPath pluginDir)
        {
            return new InstallationInfo(true, pluginDir, EmptyArray<FileSystemPath>.Instance, ZeroVersion);
        }

        [NotNull]
        private InstallationInfo ExistingInstallation(List<FileSystemPath> pluginFiles)
        {
            var parentDirs = pluginFiles.Select(f => f.Directory).Distinct().ToList();
            if (parentDirs.Count > 1)
            {
                myLogger.Warn("Plugin files detected in more than one directory.");
                return new InstallationInfo(false, FileSystemPath.Empty, pluginFiles, ZeroVersion);
            }

            if (parentDirs.Count == 0)
            {
                myLogger.Warn("Plugin files do not have parent directory (?).");
                return new InstallationInfo(false, FileSystemPath.Empty, pluginFiles, ZeroVersion);
            }

            // v1.8 is two files, v1.9 is one
            if (pluginFiles.Count == 0 || pluginFiles.Count > 2)
            {
                myLogger.Warn("Unsupported plugin file count: {0}", pluginFiles.Count);
                return new InstallationInfo(false, FileSystemPath.Empty, pluginFiles, ZeroVersion);
            }
            
            var pluginDir = parentDirs[0];
            var filenames = pluginFiles.Select(f => f.Name).ToList();

            // v1.9.0+
            if (pluginFiles.Count == 1)
            {
                if (pluginFiles[0].Name == MergedPluginFile)
                {
                    var version = GetVersionFromFile(pluginFiles[0]);
                    return new InstallationInfo(version != ZeroVersion, pluginDir, pluginFiles, version);
                }
                
                myLogger.Warn("One file found, but filename is not the same as v1.9.0+");
                return new InstallationInfo(false, FileSystemPath.Empty, pluginFiles, ZeroVersion);
            }
            
            // v1.8 probably
            if (filenames.Count == 2)
            {
                if (filenames.IsEquivalentTo(ourPluginFilesV180))
                {
                    return new InstallationInfo(true, pluginDir, pluginFiles, new Version(1, 8, 0, 0));
                }

                myLogger.Warn("Two files found, but filenames are not the same as in v1.8");
                return new InstallationInfo(false, FileSystemPath.Empty, pluginFiles, ZeroVersion);
            }
            
            return new InstallationInfo(false, FileSystemPath.Empty, pluginFiles, ZeroVersion);
        }

        private static Version GetVersionFromFile(FileSystemPath mergedFile)
        {
            var blockBuilder = new StringBuilder();
            using (var fs = mergedFile.OpenStream(FileMode.Open, FileAccess.Read))
            using (var sr = new StreamReader(fs))
            {
                string line;
                do
                {
                    line = sr.ReadLine();
                    blockBuilder.AppendLine(line);
                } while (line != null && line.StartsWith("//"));
            }

            var commentBlock = blockBuilder.ToString();
            if (string.IsNullOrWhiteSpace(commentBlock))
                return ZeroVersion;

            var match = ourVersionRegex.Match(commentBlock);
            if (!match.Success)
                return ZeroVersion;

            Version version;
            return Version.TryParse(match.Groups[1].Value, out version) ? version : ZeroVersion;
        }

        public class InstallationInfo
        {
            public readonly bool ShouldInstallPlugin;

            [NotNull]
            public readonly FileSystemPath PluginDirectory;

            [NotNull]
            public readonly ICollection<FileSystemPath> ExistingFiles;

            [NotNull]
            public readonly Version Version;

            public InstallationInfo(bool shouldInstallPlugin, [NotNull] FileSystemPath pluginDirectory,
                [NotNull] ICollection<FileSystemPath> existingFiles, [NotNull] Version version)
            {
                ShouldInstallPlugin = shouldInstallPlugin;
                PluginDirectory = pluginDirectory;
                ExistingFiles = existingFiles;
                Version = version;
            }
        }
    }
}
#endif