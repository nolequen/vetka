using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        public bool? HasUncommittedChanges()
        {
            var status = Run("status", "--porcelain", "--untracked-files=no");
            return status.IsSuccess ? status.Output.Length > 0 : (bool?)null;
        }

        private static readonly string[] WorkingTreeStatus =
            { "status", "--porcelain=v1", "-z", "--untracked-files=all" };

        private static readonly string[] PlainStatus =
            { "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames" };

        private static readonly string[] TreeStatus =
            { "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames", "--ignore-submodules=all" };

        private sealed class TreeState
        {
            public readonly HashSet<string> Changed = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Untracked = new HashSet<string>(StringComparer.Ordinal);
        }

        private TreeState ReadTree()
        {
            var status = Run(TreeStatus);
            if (!status.IsSuccess)
            {
                return null;
            }

            var state = new TreeState();
            foreach (var entry in StatusEntries(status.Output))
            {
                (entry.Index == '?' ? state.Untracked : state.Changed).Add(entry.Path);
            }

            return state;
        }

        private HashSet<string> DifferentFrom(string commit, IEnumerable<string> untracked) =>
            WithTemporaryIndex(environment =>
            {
                if (!RunWithPathspecs(environment, untracked, "add", "--intent-to-add").IsSuccess)
                {
                    return null;
                }

                var diff = RunWithEnvironment(environment, "diff", "--name-only", "-z", "--no-renames", commit);
                return diff.IsSuccess
                    ? new HashSet<string>(diff.Output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries),
                        StringComparer.Ordinal)
                    : null;
            });

        public (GitResult Result, string Top, string Prefix) ProjectLocation()
        {
            var result = Run("rev-parse", "--show-toplevel", "--show-prefix");
            var lines = result.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (!result.IsSuccess || lines.Length == 0)
            {
                return (result.IsSuccess ? GitResult.Failure("Cannot find the repository") : result, null, null);
            }

            return (result, Path.GetFullPath(lines[0]), lines.Length > 1 ? lines[1] : "");
        }

        private static readonly string[] MoveStatus =
            { "status", "--porcelain=v2", "-z", "--untracked-files=all", "--find-renames" };

        public (GitResult Result, List<GitFileChange> Changes) WorkingTreeChanges()
        {
            var result = Run(WorkingTreeStatus);
            if (!result.IsSuccess)
            {
                return (result, new List<GitFileChange>());
            }

            var changes = ParseWorkingTree(result.Output);
            var created = changes
                .Where(change => change.Status == GitStatus.Untracked || change.Status == GitStatus.Added ||
                                 change.Status == GitStatus.Renamed)
                .Select(change => change.Path)
                .ToList();
            var removed = changes.Where(change => change.Status == GitStatus.Deleted)
                .Select(change => change.Path)
                .Concat(changes.Where(change => change.OldPath != null).Select(change => change.OldPath))
                .ToList();
            if (created.Count == 0 ||
                changes.All(change => change.Status != GitStatus.Deleted && change.Status != GitStatus.Renamed))
            {
                return (result, Sorted(changes));
            }

            var staged = changes
                .Where(change => change.Status == GitStatus.Added || change.Status == GitStatus.Renamed)
                .Select(change => change.Path);
            var moves = WithTemporaryIndex(environment =>
            {
                if (!RunWithPathspecs(environment, removed.Concat(staged), "reset", "-q", "HEAD").IsSuccess ||
                    !RunWithPathspecs(environment, created, "add", "--intent-to-add").IsSuccess)
                {
                    return null;
                }

                var status = RunWithEnvironment(environment, MoveStatus);
                return status.IsSuccess ? ConfirmedMoves(status.Output) : null;
            });
            if (moves == null)
            {
                return (result, Sorted(changes));
            }

            var moved = new HashSet<string>(moves.Select(move => move.Path).Concat(moves.Select(move => move.OldPath)));
            var shown = new List<GitFileChange>();
            foreach (var change in changes.Where(change => !moved.Contains(change.Path)))
            {
                if (change.Status != GitStatus.Renamed)
                {
                    shown.Add(change);
                    continue;
                }

                if (!moved.Contains(change.OldPath))
                {
                    shown.Add(new GitFileChange(GitStatus.Deleted, change.OldPath));
                }

                shown.Add(new GitFileChange(GitStatus.Added, change.Path));
            }

            return (result, Sorted(shown.Concat(moves).ToList()));
        }

        private static List<GitFileChange> Sorted(List<GitFileChange> changes)
        {
            changes.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
            return changes;
        }

        private static IEnumerable<(char Index, char WorkTree, string Path, string OldPath)> StatusEntries(
            string output)
        {
            var tokens = output.Split('\0');
            for (var i = 0; i < tokens.Length; i++)
            {
                var entry = tokens[i];
                if (entry.Length < 4)
                {
                    continue;
                }

                var index = entry[0];
                var workTree = entry[1];
                var paired = index == 'R' || index == 'C' || workTree == 'R' || workTree == 'C';
                var oldPath = paired && i + 1 < tokens.Length ? tokens[++i] : null;
                yield return (index, workTree, entry.Substring(3), oldPath);
            }
        }

        internal static List<GitFileChange> ParseWorkingTree(string output)
        {
            var changes = new List<GitFileChange>();
            foreach (var (index, workTree, path, oldPath) in StatusEntries(output))
            {
                if (path.EndsWith("/"))
                {
                    continue;
                }

                if (oldPath != null && (index == 'R' || workTree == 'R'))
                {
                    changes.Add(new GitFileChange(
                        workTree == 'D' ? GitStatus.Deleted : GitStatus.Renamed, path, oldPath));
                    continue;
                }

                var status = index == 'C' || workTree == 'C' ? GitStatus.Added : Git.ParseStatus(index, workTree);
                if (status != GitStatus.Ignored)
                {
                    changes.Add(new GitFileChange(status, path));
                }
            }

            return changes;
        }

        internal static List<GitFileChange> ConfirmedMoves(string output)
        {
            var moves = MoveEntries(output).ToList();
            return moves.Where(move => IsConfirmedMove(move, moves))
                .Select(move => new GitFileChange(GitStatus.Renamed, move.Path, move.OldPath))
                .ToList();
        }

        private static IEnumerable<(string Path, string OldPath, int Score)> MoveEntries(string output)
        {
            var tokens = output.Split('\0');
            for (var i = 0; i < tokens.Length; i++)
            {
                if (!tokens[i].StartsWith("2 "))
                {
                    continue;
                }

                var fields = tokens[i].Split(new[] { ' ' }, 10);
                var oldPath = i + 1 < tokens.Length ? tokens[++i] : null;
                if (fields.Length < 10 || fields[1].Length < 2 || fields[1][1] != 'R' || oldPath == null)
                {
                    continue;
                }

                yield return (fields[9], oldPath, int.TryParse(fields[8].Substring(1), out var score) ? score : 0);
            }
        }

        private static bool IsConfirmedMove((string Path, string OldPath, int Score) move,
            List<(string Path, string OldPath, int Score)> moves)
        {
            if (move.Path.EndsWith(".meta") && move.OldPath.EndsWith(".meta"))
            {
                var asset = move.Path.Substring(0, move.Path.Length - ".meta".Length);
                var oldAsset = move.OldPath.Substring(0, move.OldPath.Length - ".meta".Length);
                return move.Score == 100 && moves.Any(other =>
                    other.Path == asset && other.OldPath == oldAsset ||
                    other.Path.StartsWith(asset + "/") && other.OldPath.StartsWith(oldAsset + "/"));
            }

            var meta = move.Path + ".meta";
            var oldMeta = move.OldPath + ".meta";
            return moves.Any(other => other.Path == meta || other.OldPath == oldMeta)
                ? moves.Any(other => other.Path == meta && other.OldPath == oldMeta && other.Score == 100)
                : move.Score == 100;
        }
    }
}
