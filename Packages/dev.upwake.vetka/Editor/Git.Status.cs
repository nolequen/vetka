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

                var paths = ChangedPaths(environment, commit);
                return paths == null ? null : new HashSet<string>(paths, StringComparer.Ordinal);
            });

        public (GitResult Result, string Top, string Prefix) ProjectLocation()
        {
            var result = Run("rev-parse", "--show-toplevel", "--show-prefix");
            var lines = Lines(result.Output);
            if (!result.IsSuccess || lines.Count == 0)
            {
                return (result.IsSuccess ? GitResult.Failure("Cannot find the repository") : result, null, null);
            }

            return (result, Path.GetFullPath(lines[0]), lines.Count > 1 ? lines[1] : "");
        }

        public (GitResult Result, List<GitFileChange> Changes) WorkingTreeChanges()
        {
            var result = Run(PlainStatus);
            if (!result.IsSuccess)
            {
                return (result, new List<GitFileChange>());
            }

            var changes = ParseWorkingTree(result.Output);
            var created = PathsOf(changes, GitStatus.Untracked, GitStatus.Added);
            var removed = PathsOf(changes, GitStatus.Deleted);
            if (created.Count == 0 || removed.Count == 0)
            {
                return (result, Sorted(changes));
            }

            var staged = PathsOf(changes, GitStatus.Added);
            var moves = WithTemporaryIndex(environment =>
            {
                if (!RunWithPathspecs(environment, removed.Concat(staged), "reset", "-q", "HEAD").IsSuccess ||
                    !RunWithPathspecs(environment, created, "add", "--intent-to-add").IsSuccess)
                {
                    return null;
                }

                var diff = RunWithEnvironment(environment, "diff", "--find-renames", "--name-status", "-z");
                return diff.IsSuccess ? ConfirmedMoves(diff.Output) : null;
            });
            if (moves == null)
            {
                return (result, Sorted(changes));
            }

            var moved = new HashSet<string>(moves.Select(move => move.Path).Concat(moves.Select(move => move.OldPath)));
            return (result, Sorted(changes.Where(change => !moved.Contains(change.Path)).Concat(moves).ToList()));
        }

        private static List<string> PathsOf(IEnumerable<GitFileChange> changes, params GitStatus[] statuses) =>
            changes.Where(change => statuses.Contains(change.Status)).Select(change => change.Path).ToList();

        private static List<GitFileChange> Sorted(List<GitFileChange> changes)
        {
            changes.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
            return changes;
        }

        private static IEnumerable<(char Index, char WorkTree, string Path)> StatusEntries(string output) =>
            output.Split('\0')
                .Where(entry => entry.Length >= 4)
                .Select(entry => (entry[0], entry[1], entry.Substring(3)));

        private static List<GitFileChange> ParseWorkingTree(string output) =>
            StatusEntries(output)
                .Where(entry => !entry.Path.EndsWith("/"))
                .Select(entry => new GitFileChange(ParseStatus(entry.Index, entry.WorkTree), entry.Path))
                .ToList();

        private static List<GitFileChange> ConfirmedMoves(string output)
        {
            var moves = NameStatus(output.Split('\0'))
                .Where(entry => entry.Status[0] == 'R')
                .Select(entry => (entry.Path, entry.OldPath, Score: RenameScore(entry.Status)))
                .ToList();
            var byPath = new Dictionary<string, (string OldPath, int Score)>(StringComparer.Ordinal);
            foreach (var move in moves)
            {
                byPath[move.Path] = (move.OldPath, move.Score);
            }

            var oldPaths = new HashSet<string>(moves.Select(move => move.OldPath), StringComparer.Ordinal);
            return moves.Where(move => IsConfirmedMove(move, moves, byPath, oldPaths))
                .Select(move => new GitFileChange(GitStatus.Renamed, move.Path, move.OldPath))
                .ToList();
        }

        private static bool IsConfirmedMove((string Path, string OldPath, int Score) move,
            List<(string Path, string OldPath, int Score)> moves,
            Dictionary<string, (string OldPath, int Score)> byPath, HashSet<string> oldPaths)
        {
            if (move.Path.EndsWith(".meta", StringComparison.Ordinal) &&
                move.OldPath.EndsWith(".meta", StringComparison.Ordinal))
            {
                var asset = move.Path.Substring(0, move.Path.Length - ".meta".Length);
                var oldAsset = move.OldPath.Substring(0, move.OldPath.Length - ".meta".Length);
                if (move.Score != 100)
                {
                    return false;
                }

                if (byPath.TryGetValue(asset, out var paired) && paired.OldPath == oldAsset)
                {
                    return true;
                }

                var prefix = asset + "/";
                var oldPrefix = oldAsset + "/";
                return moves.Any(other => other.Path.StartsWith(prefix, StringComparison.Ordinal) &&
                                          other.OldPath.StartsWith(oldPrefix, StringComparison.Ordinal));
            }

            var meta = move.Path + ".meta";
            var oldMeta = move.OldPath + ".meta";
            if (!byPath.TryGetValue(meta, out var metaMove) && !oldPaths.Contains(oldMeta))
            {
                return move.Score == 100;
            }

            return metaMove.OldPath == oldMeta && metaMove.Score == 100;
        }
    }
}
