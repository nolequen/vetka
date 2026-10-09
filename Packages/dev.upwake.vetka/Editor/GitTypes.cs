using System;
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
        Ignored,
        Unknown,
        Broken
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

        private const int CancelledExitCode = -2;

        private const int RejectedExitCode = -3;
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
            string text)
        {
            Hash = hash;
            Author = author;
            Date = date;
            Summary = summary;
            FileName = fileName;
            Number = number;
            Text = text;
        }

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
        public GitCommit(string hash, string author, string date, string subject, bool isPushed)
        {
            Hash = hash;
            Author = author;
            Date = date;
            Subject = subject;
            IsPushed = isPushed;
        }

        public string Hash { get; }
        public string Author { get; }
        public string Date { get; }
        public string Subject { get; }

        public bool IsPushed { get; }
    }
}
