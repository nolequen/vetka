using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Upwake.Vetka
{
    internal sealed class FileBackup : IDisposable
    {
        private readonly string _root;
        private readonly string _prefix;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"Vetka-{Guid.NewGuid():N}");
        private readonly List<(string Path, string Copy)> _files = new List<(string Path, string Copy)>();
        private readonly List<string> _createdDirectories = new List<string>();
        private bool _keep;

        public FileBackup(string root)
        {
            _root = root;
            _prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                      Path.DirectorySeparatorChar;
        }

        public string Keep()
        {
            _keep = true;
            return _directory;
        }

        public string Save(IEnumerable<string> paths)
        {
            try
            {
                foreach (var path in paths.Distinct())
                {
                    var file = Path.GetFullPath(Path.Combine(_root, path));
                    if (!file.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!File.Exists(file))
                    {
                        _files.Add((path, null));
                        RememberMissingDirectories(file);
                        continue;
                    }

                    Directory.CreateDirectory(_directory);
                    var copy = Path.Combine(_directory, _files.Count.ToString());
                    File.Copy(file, copy);
                    _files.Add((path, copy));
                }

                return null;
            }
            catch (Exception exception)
            {
                return $"Cannot save a copy of the files it changes: {exception.Message}";
            }
        }

        public List<string> Restore()
        {
            var failed = new List<string>();
            foreach (var (path, copy) in _files)
            {
                var file = Path.GetFullPath(Path.Combine(_root, path));
                try
                {
                    if (copy == null)
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                        }

                        continue;
                    }

                    if (File.Exists(file) && SameContent(file, copy))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    if (File.Exists(file))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    File.Copy(copy, file, true);
                }
                catch (Exception)
                {
                    failed.Add(path);
                }
            }

            DeleteEmptyFolders(_createdDirectories, false);
            return failed;
        }

        public static void DeleteEmptyFolders(IEnumerable<string> folders, bool keepFoldersWithMeta)
        {
            foreach (var directory in folders.OrderByDescending(directory => directory.Length))
            {
                try
                {
                    if (Directory.Exists(directory) && !(keepFoldersWithMeta && File.Exists(directory + ".meta")) &&
                        !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Vetka: cannot delete {directory}: {exception.Message}");
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_keep && Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, true);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Vetka: cannot delete a temporary folder {_directory}: {exception.Message}");
            }
        }

        private void RememberMissingDirectories(string file)
        {
            var directory = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(directory) && directory.Length > _root.Length && !Directory.Exists(directory))
            {
                if (!_createdDirectories.Contains(directory))
                {
                    _createdDirectories.Add(directory);
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        private static bool SameContent(string left, string right)
        {
            var first = new FileInfo(left);
            var second = new FileInfo(right);
            return first.Length == second.Length && File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
        }
    }
}
