using System;
using System.Collections.Generic;
using System.Linq;

namespace Upwake.Vetka
{
    internal enum GitStatus
    {
        Added,
        Modified,
        Deleted,
        Renamed,
        Copied,
        TypeChanged,
        Unmerged,
        Untracked,
        Unknown
    }

    internal readonly struct GitResult
    {
        public GitResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output ?? "";
            Error = error ?? "";
        }

        public int ExitCode { get; }

        public string Output { get; }

        public string Error { get; }

        public bool IsSuccess => ExitCode == 0;

        public bool IsCancelled => ExitCode == CancelledExitCode;

        public bool IsRejected => ExitCode == RejectedExitCode;

        public bool IsDone => IsSuccess || ExitCode == DoneExitCode;

        public string Message
        {
            get
            {
                if (IsSuccess)
                {
                    if (!string.IsNullOrEmpty(Output))
                    {
                        return Output;
                    }

                    return !string.IsNullOrEmpty(Error) ? Error : "Done";
                }

                var lines = new[] { Error, Output }
                    .SelectMany(part => part.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    .Select(line => line.TrimEnd())
                    .Where(line => line.Length > 0 && !line.StartsWith("hint:"))
                    .Select(line => line.StartsWith("fatal: ") || line.StartsWith("error: ")
                        ? line.Substring(line.IndexOf(' ') + 1)
                        : line)
                    .ToList();
                return lines.Count > 0 ? string.Join("\n", lines) : $"git exited with code {ExitCode}";
            }
        }

        public static GitResult Success(string message) => new GitResult(0, message, "");

        public static GitResult Failure(string message) => new GitResult(-1, "", message);

        public static GitResult Cancelled(string message) => new GitResult(CancelledExitCode, "", message);

        public static GitResult Rejected(string message) => new GitResult(RejectedExitCode, "", message);

        public static GitResult Done(string message) => new GitResult(DoneExitCode, "", message);

        private const int CancelledExitCode = -2;

        private const int RejectedExitCode = -3;

        private const int DoneExitCode = -4;
    }

    internal readonly struct GitIdentity
    {
        public GitIdentity(string name, string email)
        {
            Name = name?.Trim() ?? "";
            Email = email?.Trim() ?? "";
        }

        public string Name { get; }
        public string Email { get; }

        public bool IsComplete => !string.IsNullOrEmpty(Name) && !string.IsNullOrEmpty(Email);
    }

    internal readonly struct GitBranch
    {
        public GitBranch(string name, bool isRemote, bool isCurrent, string upstream, string track, string reference)
        {
            Name = name;
            Ref = reference;
            IsRemote = isRemote;
            IsCurrent = isCurrent;
            Upstream = upstream;
            Track = track;
        }

        public string Name { get; }
        public string Ref { get; }
        public bool IsRemote { get; }
        public bool IsCurrent { get; }
        public string Upstream { get; }
        public string Track { get; }
    }

    internal readonly struct GitFileChange
    {
        public GitFileChange(GitStatus status, string path, string oldPath = null)
        {
            Status = status;
            Path = path;
            OldPath = oldPath;
        }

        public GitStatus Status { get; }
        public string Path { get; }
        public string OldPath { get; }
    }

    internal readonly struct GitBlameLine
    {
        public GitBlameLine(string hash, string author, DateTime date, string summary, string fileName, int number,
            string text, string previous, string previousFileName)
        {
            Hash = hash;
            Author = author;
            Date = date;
            Summary = summary;
            FileName = fileName;
            Number = number;
            Text = text;
            Previous = previous;
            PreviousFileName = previousFileName;
        }

        public string Previous { get; }
        public string PreviousFileName { get; }

        public string Hash { get; }
        public string Author { get; }
        public DateTime Date { get; }
        public string Summary { get; }
        public string FileName { get; }
        public int Number { get; }
        public string Text { get; }
        public bool IsCommitted => !string.IsNullOrEmpty(Hash) && Hash.Trim('0').Length > 0;
    }

    internal readonly struct GitStash
    {
        public GitStash(string reference, string date, string message, string hash)
        {
            Reference = reference;
            Date = date;
            Message = message;
            Hash = hash;
        }

        public string Reference { get; }
        public string Date { get; }
        public string Message { get; }
        public string Hash { get; }
    }

    internal readonly struct GitPushTarget
    {
        public GitPushTarget(string remote, string branch, bool exists)
        {
            Remote = remote;
            Branch = branch;
            Exists = exists;
        }

        public string Remote { get; }
        public string Branch { get; }
        public bool Exists { get; }
        public string Name => $"{Remote}/{Branch}";
        public string TrackingRef => $"refs/remotes/{Remote}/{Branch}";
    }

    internal readonly struct GitInterruption
    {
        public GitInterruption(string description, string time, string stash, string stashMessage, string inProgress,
            string markerPath)
        {
            Description = description;
            Time = time;
            Stash = stash;
            StashMessage = stashMessage;
            InProgress = inProgress;
            MarkerPath = markerPath;
        }

        public string Description { get; }
        public string Time { get; }
        public string Stash { get; }
        public string StashMessage { get; }
        public string InProgress { get; }
        public string MarkerPath { get; }
    }

    internal readonly struct GitCommit
    {
        public GitCommit(string hash, string author, string date, string subject, bool isPushed,
            IReadOnlyList<string> tags)
        {
            Hash = hash;
            Author = author;
            Date = date;
            Subject = subject;
            IsPushed = isPushed;
            Tags = tags;
        }

        public string Hash { get; }
        public string Author { get; }
        public string Date { get; }
        public string Subject { get; }

        public bool IsPushed { get; }
        public IReadOnlyList<string> Tags { get; }
    }

    internal sealed class GitComparison
    {
        public GitResult Result;
        public string Current;
        public string Tip;
        public int OnlyHereCount;
        public List<GitCommit> OnlyHere = new List<GitCommit>();
        public int OnlyCurrentCount;
        public List<GitCommit> OnlyCurrent = new List<GitCommit>();
        public List<GitFileChange> Files = new List<GitFileChange>();
    }

    internal enum IncomingMark
    {
        Conflict,
        ConflictsWithYourChanges,
        InTheWay,
        InUse,
        HiddenChange,
        MayConflict,
        ChangedLocally,
        Unsaved,
        None
    }

    internal sealed class IncomingCache
    {
        public string Head;
        public string Tip;
        public string ResultTree;
        public bool Exact;
        public bool Partial;
        public bool ConflictsUnlisted;
        public string Note;
        public IReadOnlyList<GitFileChange> Files = new List<GitFileChange>();
        public IReadOnlyList<string> Conflicted = new List<string>();
        public IReadOnlyList<string> MayConflict = new List<string>();
        public IReadOnlyList<string> BothSides = new List<string>();
    }

    internal sealed class GitIncoming
    {
        public GitResult Result = GitResult.Success("");
        public string Branch;
        public string Upstream;
        public bool LocalUpstream;
        public string Head;
        public string Tip;
        public string MergeBase;
        public string ResultTree;
        public string DiffFrom;
        public string DiffTo;
        public string DiffLabel;
        public string Prefix = "";
        public string Summary;
        public string Action;
        public int IncomingCount;
        public List<GitCommit> Incoming = new List<GitCommit>();
        public int FileCount;
        public List<GitFileChange> Files = new List<GitFileChange>();
        public Dictionary<string, IncomingMark> Marks = new Dictionary<string, IncomingMark>(StringComparer.Ordinal);
        public List<string> Conflicted = new List<string>();
        public List<string> Refusals = new List<string>();
        public List<string> Warnings = new List<string>();
        public string Note;
        public DateTime? LastFetch;
        public IncomingCache Cache;

        public IncomingMark MarkOf(GitFileChange file) =>
            Marks.TryGetValue(file.Path, out var mark) ? mark : IncomingMark.None;
    }
}
