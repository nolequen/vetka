using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        private const int MaxLoggedLength = 4000;

        private static readonly string[] CommonArguments =
        {
            "-c", "core.quotepath=false",
            "-c", "log.showSignature=false",
            "-c", "diff.relative=false",
            "-c", "diff.noprefix=false",
            "-c", "i18n.logOutputEncoding=UTF-8",
            "-c", "i18n.commitEncoding=UTF-8",
            "-c", "rebase.updateRefs=false"
        };

        private static readonly string[] RepositoryVariables =
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE", "GIT_PREFIX"
        };

        internal Action<IReadOnlyList<string>> BeforeCommand { get; set; }

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private GitResult Run(params string[] arguments) => Execute(null, false, arguments);

        private GitResult RunWithProgress(params string[] arguments) => Execute(null, true, arguments);

        private GitResult RunWithProgress(IReadOnlyDictionary<string, string> environment, params string[] arguments) =>
            Execute(environment, true, arguments);

        private GitResult RunWithEnvironment(IReadOnlyDictionary<string, string> environment,
            params string[] arguments) => Execute(environment, false, arguments);

        private GitResult Execute(IReadOnlyDictionary<string, string> environment, bool reportProgress,
            string[] arguments)
        {
            var retry = Kind(arguments) == CommandKind.Changing && MayRepeat(arguments);
            var waited = Stopwatch.StartNew();
            var delay = TimeSpan.FromMilliseconds(100);
            while (true)
            {
                var result = ExecuteOnce(environment, reportProgress, arguments);
                if (!retry || result.IsSuccess || result.IsCancelled ||
                    result.Error.IndexOf("index.lock", StringComparison.Ordinal) < 0 ||
                    waited.Elapsed + delay > LockRetryTimeout)
                {
                    return result;
                }

                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }

        private GitResult ExecuteOnce(IReadOnlyDictionary<string, string> environment, bool reportProgress,
            string[] arguments)
        {
            var gitPath = GitPath;
            if (string.IsNullOrEmpty(gitPath) || !File.Exists(gitPath))
            {
                return GitResult.Failure("Git not found, set its path in Tools/Git/Settings");
            }

            var command = "git " + string.Join(" ", arguments);
            var context = OperationContext.Current;
            if (context != null && context.ShouldStop)
            {
                return GitResult.Cancelled($"'{command}' cancelled");
            }

            var kind = Kind(arguments);
            var startInfo = new ProcessStartInfo
            {
                FileName = gitPath,
                WorkingDirectory = _projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8
            };

            foreach (var argument in CommonArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (kind != CommandKind.Network)
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("submodule.recurse=false");
            }

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            SetVariables(startInfo, _environment);
            foreach (var variable in RepositoryVariables)
            {
                startInfo.Environment.Remove(variable);
            }

            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            if (kind == CommandKind.Reading)
            {
                startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            }

            SetVariables(startInfo, environment);

            try
            {
                BeforeCommand?.Invoke(arguments);
                using var process = new Process { StartInfo = startInfo };
                process.Start();
                using var job = ProcessJob.Attach(process);

                var activity = new Activity();
                var reporter = reportProgress ? context?.Reporter ?? IgnoreProgress : null;
                var outputTask = Task.Run(() => Read(new ActivityReader(process.StandardOutput, activity), reporter));
                var errorTask = Task.Run(() => Read(new ActivityReader(process.StandardError, activity), reporter));

                var stopped = WaitForExit(process, kind, activity, context, command);
                if (stopped.HasValue)
                {
                    KillTree(process, job);
                    AwaitReaders(outputTask, errorTask);
                    return stopped.Value;
                }

                process.WaitForExit();
                var output = outputTask.GetAwaiter().GetResult().TrimEnd();
                var error = errorTask.GetAwaiter().GetResult().Trim();

                var result = new GitResult(process.ExitCode, output, error);
                if (!Verbose)
                {
                    return result;
                }

                var logged = result.Message.Length > MaxLoggedLength
                    ? result.Message.Substring(0, MaxLoggedLength) + $"\n... ({result.Message.Length} characters)"
                    : result.Message;
                if (result.IsSuccess)
                {
                    Debug.Log($"{command}\n[{process.ExitCode}] {logged}");
                }
                else
                {
                    Debug.LogWarning($"{command}\n[{process.ExitCode}] {logged}");
                }

                return result;
            }
            catch (Exception exception)
            {
                return GitResult.Failure($"'{command}' could not be started: {exception.Message}");
            }
        }

        private static void SetVariables(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string> variables)
        {
            if (variables == null)
            {
                return;
            }

            foreach (var variable in variables)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        internal enum CommandKind
        {
            Reading,
            Network,
            Changing
        }

        private static readonly HashSet<string> ReadingCommands = new HashSet<string>
        {
            "--version", "blame", "check-ref-format", "diff", "for-each-ref", "log", "ls-files", "ls-tree", "merge-base",
            "reflog", "rev-list", "rev-parse", "show", "status", "symbolic-ref"
        };

        private const int PollMilliseconds = 100;

        internal static TimeSpan ReadingTimeout { get; set; } = TimeSpan.FromMinutes(5);

        internal static TimeSpan NetworkSilenceTimeout { get; set; } = TimeSpan.FromMinutes(5);

        internal static TimeSpan LockRetryTimeout { get; set; } = TimeSpan.FromSeconds(5);

        private static int CommandIndex(IReadOnlyList<string> arguments)
        {
            var i = 0;
            while (i < arguments.Count && arguments[i] == "-C")
            {
                i += 2;
            }

            return i;
        }

        internal static bool MayRepeat(IReadOnlyList<string> arguments)
        {
            var i = CommandIndex(arguments);
            if (i >= arguments.Count)
            {
                return false;
            }

            var next = i + 1 < arguments.Count ? arguments[i + 1] : null;
            return arguments[i] switch
            {
                "stash" => next != "push",
                "rebase" => next == "--abort",
                "merge" => next == "--abort",
                _ => true
            };
        }

        internal static CommandKind Kind(IReadOnlyList<string> arguments)
        {
            var i = CommandIndex(arguments);
            if (i >= arguments.Count)
            {
                return CommandKind.Changing;
            }

            var name = arguments[i];
            if (name == "fetch" || name == "push")
            {
                return CommandKind.Network;
            }

            return ReadingCommands.Contains(name) ? CommandKind.Reading : CommandKind.Changing;
        }

        private static GitResult? WaitForExit(Process process, CommandKind kind, Activity activity,
            OperationContext context, string command)
        {
            var running = Stopwatch.StartNew();
            while (!process.WaitForExit(PollMilliseconds))
            {
                if (context != null && context.ShouldStop)
                {
                    return GitResult.Cancelled($"'{command}' cancelled");
                }

                if (kind == CommandKind.Reading && running.Elapsed > ReadingTimeout)
                {
                    return GitResult.Failure($"'{command}' timed out after {(int)ReadingTimeout.TotalSeconds} s");
                }

                if (kind == CommandKind.Network && activity.Silence > NetworkSilenceTimeout)
                {
                    return GitResult.Failure(
                        $"'{command}' stopped: no response for {(int)NetworkSilenceTimeout.TotalSeconds} s");
                }
            }

            return null;
        }

        private static void KillTree(Process process, ProcessJob job)
        {
            try
            {
                job?.Terminate();
                if (process.HasExited)
                {
                    return;
                }

                if (IsWindows)
                {
                    using var taskkill = Process.Start(new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                        Arguments = $"/T /F /PID {process.Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    taskkill?.WaitForExit(10000);
                }

                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Vetka: cannot stop git: {exception.Message}");
            }
        }

        private static void AwaitReaders(params Task[] readers)
        {
            try
            {
                Task.WaitAll(readers, 2000);
            }
            catch (AggregateException exception)
            {
                Debug.LogWarning($"Vetka: cannot read the output of a stopped git: {exception.InnerException?.Message}");
            }
        }

        private sealed class ProcessJob : IDisposable
        {
            private IntPtr _handle;

            private ProcessJob(IntPtr handle)
            {
                _handle = handle;
            }

            public static ProcessJob Attach(Process process)
            {
                if (!IsWindows)
                {
                    return null;
                }

                try
                {
                    var handle = CreateJobObject(IntPtr.Zero, null);
                    if (handle == IntPtr.Zero)
                    {
                        return null;
                    }

                    if (AssignProcessToJobObject(handle, process.Handle))
                    {
                        return new ProcessJob(handle);
                    }

                    CloseHandle(handle);
                    return null;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Vetka: cannot track the processes started by git: {exception.Message}");
                    return null;
                }
            }

            public void Terminate()
            {
                if (_handle != IntPtr.Zero)
                {
                    TerminateJobObject(_handle, 1);
                }
            }

            public void Dispose()
            {
                if (_handle == IntPtr.Zero)
                {
                    return;
                }

                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);
        }

        private sealed class Activity
        {
            private readonly object _gate = new object();
            private readonly Stopwatch _since = Stopwatch.StartNew();

            public TimeSpan Silence
            {
                get
                {
                    lock (_gate)
                    {
                        return _since.Elapsed;
                    }
                }
            }

            public void Touch()
            {
                lock (_gate)
                {
                    _since.Restart();
                }
            }
        }

        private sealed class ActivityReader : TextReader
        {
            private readonly TextReader _inner;
            private readonly Activity _activity;

            public ActivityReader(TextReader inner, Activity activity)
            {
                _inner = inner;
                _activity = activity;
            }

            public override int Peek() => _inner.Peek();

            public override int Read()
            {
                var value = _inner.Read();
                _activity.Touch();
                return value;
            }

            public override int Read(char[] buffer, int index, int count)
            {
                var read = _inner.Read(buffer, index, count);
                _activity.Touch();
                return read;
            }
        }

        private static GitResult Interruptible(string what, Func<GitResult> run)
        {
            var result = OperationContext.Interruptible(run, out var cancelled);
            return cancelled ? GitResult.Cancelled($"{what} cancelled") : result;
        }

        private static readonly Action<float, string> IgnoreProgress = (_, __) => { };

        private static string Read(TextReader reader, Action<float, string> reporter) =>
            reporter != null ? ReadProgress(reader, reporter) : reader.ReadToEnd();

        internal static string ReadProgress(TextReader reader, Action<float, string> report)
        {
            var messages = new StringBuilder();
            var line = new StringBuilder();
            var buffer = new char[1024];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == '\r' || buffer[i] == '\n')
                    {
                        HandleProgressLine(line.ToString(), messages, report);
                        line.Clear();
                    }
                    else
                    {
                        line.Append(buffer[i]);
                    }
                }
            }

            HandleProgressLine(line.ToString(), messages, report);
            return messages.ToString();
        }

        private static void HandleProgressLine(string rawLine, StringBuilder messages, Action<float, string> report)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                return;
            }

            var progress = ProgressLine.Match(line);
            if (progress.Success)
            {
                var percent = int.Parse(progress.Groups[2].Value);
                report(Mathf.Clamp01(percent / 100f), $"{progress.Groups[1].Value} {percent}%");
                return;
            }

            if (FinishedStageLine.IsMatch(line) || line.StartsWith("Delta compression using"))
            {
                return;
            }

            messages.AppendLine(line);
        }

        private static readonly Regex ProgressLine =
            new Regex(@"^(?:remote:\s*)?([A-Za-z][A-Za-z ]*?):\s+(\d{1,3})%");

        private static readonly Regex FinishedStageLine =
            new Regex(@"^(?:remote:\s*)?[A-Za-z][A-Za-z ]*?:\s.*done\.$");
    }
}
