﻿// RequireJS.NET
// Copyright VeriTech.io
// http://veritech.io
// Dual licensed under the MIT and GPL licenses:
// http://www.opensource.org/licenses/mit-license.php
// http://www.gnu.org/licenses/gpl.html

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using RequireJsNet.Compressor.AutoDependency;
using RequireJsNet.Compressor.Models;
using RequireJsNet.Configuration;
using RequireJsNet.Helpers;
using RequireJsNet.Models;
using Microsoft.Build.Framework;

namespace RequireJsNet.Compressor.RequireProcessing
{
    internal class AutoBundleConfigProcessor : ConfigProcessor
    {
        private Encoding encoding;
        private Microsoft.Build.Utilities.TaskLoggingHelper Log;
        private MessageImportance LogLevel;

        public AutoBundleConfigProcessor(string projectPath, string packagePath, string entryPointOverride, List<string> filePaths, Encoding encoding, Microsoft.Build.Utilities.TaskLoggingHelper log = null, MessageImportance logLevel = MessageImportance.Low)
        {
            ProjectPath = projectPath;
            FilePaths = filePaths;
            OutputPath = projectPath;
            EntryOverride = entryPointOverride;
            this.encoding = encoding;
            this.Log = log;
            this.LogLevel = logLevel;

            if (!string.IsNullOrWhiteSpace(packagePath))
            {
                OutputPath = packagePath;
            }

            EntryPoint = this.GetEntryPointPath();

            Log?.LogMessage(LogLevel, "Autobundler initialized");
            Log?.LogMessage(LogLevel, $" - Project path: {projectPath}");
            Log?.LogMessage(LogLevel, $" - Package path: {packagePath}");
            Log?.LogMessage(LogLevel, $" - EP Override : {entryPointOverride}");
            Log?.LogMessage(LogLevel, $" - Encoding    : {encoding.EncodingName}");
            Log?.LogMessage(LogLevel, $" - Entrypoint  : {EntryPoint}");
            Log?.LogMessage(LogLevel, $" - Output path : {OutputPath}");
        }

        public override List<Bundle> ParseConfigs()
        {
            if (!Directory.Exists(ProjectPath))
            {
                throw new DirectoryNotFoundException($"Could not find project directory '{ProjectPath}'.");
            }

            FindConfigs();

            var loader = new ConfigLoader(
                FilePaths,
                new ExceptionThrowingLogger(),
                new ConfigLoaderOptions { ProcessBundles = true });

            Configuration = loader.Get();

            var bundles = Configuration.AutoBundles.Bundles
                .Select(autoBundle => createBundleOf(autoBundle))
                .ToList();

            this.WriteOverrideConfigs(bundles);

            return bundles;
        }

        private Bundle createBundleOf(AutoBundle autoBundle)
        {
            Log?.LogMessage(LogLevel, $"Creating bundle {autoBundle.Id} for {autoBundle.Includes.Count} includes at {autoBundle.OutputPath}");

            var excludedFiles = new HashSet<string>(physicalPathsOf(autoBundle.Excludes), StringComparer.InvariantCultureIgnoreCase);
            Log?.LogMessage(LogLevel, $" - Identified {excludedFiles.Count} excluded files.");

            var files = physicalPathsOf(autoBundle.Includes, excludedFiles).Distinct().ToList();
            Log?.LogMessage(LogLevel, $" - Looked up physical paths of {files.Count} files.");

            // check if provided paths make sense
            if (files.Count == 0 || files[0] == null)
            {
                throw new ArgumentException($"Error for autoBundle {autoBundle.Id}: "
                    + "Provided list of includes: \"" + string.Join(";", autoBundle.Includes) + "\""
                    + (excludedFiles.Count > 0 ? (" without excludes: \"" + string.Join(";", excludedFiles)) + "\"" : "")
                    + " results in empty autoBundle.");
            }

            var requiredFiles = enumerateDependenciesOf(files, excludedFiles);
            Log?.LogMessage(LogLevel, $" - Enumerated {requiredFiles.Count} dependencies.");

            var fileSpecList = requiredFilesByDependency(requiredFiles, excludedFiles, autoBundle.CompressionType);
            Log?.LogMessage(LogLevel, $" - Created {fileSpecList.Count} fileSpecLists.");
            foreach (var file in fileSpecList) Log?.LogMessage(LogLevel, $"    - {file.FileName}: {file.FileContent.Length} chars");

            return new Bundle
            {
                Files = fileSpecList,
                Output = this.GetOutputPath(autoBundle.OutputPath, autoBundle.Id),
                ContainingConfig = autoBundle.ContainingConfig,
                BundleId = autoBundle.Id
            };
        }

        private IEnumerable<string> physicalPathsOf(IEnumerable<AutoBundleItem> autoBundleItems)
        {
            var baseUrl = Path.Combine(ProjectPath, "Scripts");
            foreach (var item in autoBundleItems)
            {
                // check if the file path is actually an URL
                if (!string.IsNullOrEmpty(item.File) && !item.File.Contains("?"))
                {
                    var path = ScriptProcessor.ExpandPaths(item.File, Configuration);
                    yield return this.ResolvePhysicalPath(path, baseUrl);
                }
                else if (!string.IsNullOrEmpty(item.Directory))
                {
                    var absDirectory = this.GetAbsoluteDirectory(item.Directory);

                    // not using filter for this since we're going to use the one the user provided in the future
                    var dirFiles = Directory.GetFiles(absDirectory, "*", SearchOption.AllDirectories).Where(r => Path.GetExtension(r) == ".js").ToList();
                    foreach (var file in dirFiles)
                    {
                        yield return file;
                    }
                }
            }
        }

        private IEnumerable<string> physicalPathsOf(IEnumerable<AutoBundleItem> autoBundleItems, HashSet<string> excludedFiles)
        {
            var baseUrl = Path.Combine(ProjectPath, "Scripts");
            foreach (var item in autoBundleItems)
            {
                // check if the file path is actually an URL
                if (!string.IsNullOrEmpty(item.File) && !item.File.Contains("?"))
                {
                    item.File = ScriptProcessor.ExpandPaths(item.File, Configuration);

                    if (!excludedFiles.Contains(item.File))
                    {
                        var physicalPath = this.ResolvePhysicalPath(item.File, baseUrl);
                        if (physicalPath == null) Log?.LogError($"Could not to resolve path for {item.File}. File does not exist in {baseUrl}! Did you forget to include it in the project?");
                        yield return physicalPath;
                    }
                    else
                    {
                        Log?.LogMessage(LogLevel, $"    - EXCLUDING {item.File}");
                    }
                }
                else if (!string.IsNullOrEmpty(item.Directory))
                {
                    item.Directory = ScriptProcessor.ExpandPaths(item.Directory, Configuration);

                    var absDirectory = this.GetAbsoluteDirectory(item.Directory);
                    Log?.LogMessage(LogLevel, $"    - Directory '{item.Directory}' -> {absDirectory}");

                    // not using filter for this since we're going to use the one the user provided in the future
                    var dirFiles = Directory.GetFiles(absDirectory, "*", SearchOption.AllDirectories).Where(r => Path.GetExtension(r) == ".js").ToList();
                    foreach (var file in dirFiles)
                    {
#warning We try to match absolute paths here while we match relative paths above?!
                        if (!excludedFiles.Contains(file))
						{
                            yield return file;
                        }
                        else
                        {
                            Log?.LogMessage(LogLevel, $"    - EXCLUDING {file} from {item.Directory}");
                        }
                    }
                }
            }
        }

        private List<RequireFile> enumerateDependenciesOf(IEnumerable<string> files, HashSet<string> excludedFiles)
        {
            var processedFiles = new List<RequireFile>();
            var pendingFiles = new Queue<string>();
            this.enqueueNewFiles(files, pendingFiles, processedFiles);

            while (pendingFiles.Any())
            {
                var file = pendingFiles.Dequeue();

                var useShallowExcludes = false;
                if (!useShallowExcludes && excludedFiles.Contains(file))
                    continue;

                var requireFile = enumerateDependenciesOf(file);

                processedFiles.Add(requireFile);
                this.enqueueNewFiles(requireFile.Dependencies, pendingFiles, processedFiles);
            }

            return processedFiles;
        }

        private RequireFile enumerateDependenciesOf(string scriptFile)
        {
            var scriptText = File.ReadAllText(scriptFile, encoding);
            var relativePath = PathHelpers.GetRelativePath(scriptFile, EntryPoint);

            var processor = new ScriptProcessor(relativePath, scriptText, Configuration);
            processor.Process();

            var baseUrl = Path.Combine(ProjectPath, "Scripts");
            var dependencies = processor.Dependencies
                .Select(r => this.ResolvePhysicalPath(r, baseUrl))
                .Where(r => r != null)
                .Distinct()
                .ToList();

            return new RequireFile
            {
                Name = scriptFile,
                Content = processor.ProcessedString,
                Dependencies = dependencies
            };
        }

        private static List<FileSpec> requiredFilesByDependency(IEnumerable<RequireFile> files, HashSet<string> excludedFiles, string compressionType)
        {
            var requiredFiles = new List<RequireFile>(files);

            var fileSpecList = new List<FileSpec>();
            while (requiredFiles.Any())
            {
                var addedFiles = fileSpecList.Select(r => r.FileName).ToList();
                var noDeps = requiredFiles.Where(r => !r.Dependencies.Any()
                                                    || r.Dependencies.All(x => addedFiles.Contains(x) || excludedFiles.Contains(x))).ToList();
                if (!noDeps.Any())
                {
                    noDeps = requiredFiles.ToList();
                }

                foreach (var requireFile in noDeps)
                {
                    fileSpecList.Add(new FileSpec(requireFile.Name, compressionType) { FileContent = requireFile.Content });
                    requiredFiles.Remove(requireFile);
                }
            }

            return fileSpecList;
        }

        private void WriteOverrideConfigs(List<Bundle> bundles)
        {
            var groupings = bundles.GroupBy(r => r.ContainingConfig).ToList();
            foreach (var grouping in groupings)
            {
                var path = RequireJsNet.Helpers.PathHelpers.GetOverridePath(grouping.Key);
                Log?.LogMessage(LogLevel, $"Writing Require.JS override config to {path}");
                var writer = WriterFactory.CreateWriter(path, null);
                var collection = this.ComposeCollection(grouping.ToList());
                writer.WriteConfig(collection);    
            }
        }

        private ConfigurationCollection ComposeCollection(List<Bundle> bundles)
        {
            var conf = new ConfigurationCollection();
            conf.Overrides = new List<CollectionOverride>();
            foreach (var bundle in bundles)
            {
                Log?.LogMessage($" - Composing scripts for bundle {bundle.BundleId}");
                var scripts = bundle.Files.Select(r => PathHelpers.GetRequireRelativePath(EntryPoint, r.FileName)).ToList();
                var paths = new RequirePaths
                                {
                                    PathList = new List<RequirePath>()
                                };
                foreach (var script in scripts)
                {
                    var path = PathHelpers.GetRequireRelativePath(EntryPoint, bundle.Output);
                    paths.PathList.Add(new RequirePath(script, path));
                    Log?.LogMessage($"    - {script} -> {path}");
                }

                var over = new CollectionOverride
                               {
                                   BundleId = bundle.BundleId,
                                   BundledScripts = scripts,
                                   Paths = paths
                               };
                conf.Overrides.Add(over);
            }

            return conf;
        }

        private void enqueueNewFiles(IEnumerable<string> files, Queue<string> pendingQueue, List<RequireFile> processedFiles)
        {
            foreach (var file in files)
            {
                if (!processedFiles.Where(r => r.Name.ToLower() == file.ToLower()).Any()
                    && !pendingQueue.Where(r => r.ToLower() == file.ToLower()).Any())
                {
                    pendingQueue.Enqueue(file);
                }
            }
        }

        private string GetAbsoluteDirectory(string relativeDirectory)
        {
            string entry = this.EntryPoint;

            relativeDirectory = relativeDirectory.Replace("/", "\\");
            if (relativeDirectory.StartsWith("\\"))
            {
                relativeDirectory = relativeDirectory.Substring(1);
            }

            return Path.Combine(entry, relativeDirectory);
        }
    }
}
