using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [InitializeOnLoad]
    internal static class GitOperations
    {
        private sealed class Operation
        {
            public string Title;
            public Func<bool> Prepare;
            public Func<object> Work;
            public Action<object> Completed;
            public Action<Exception> Failed;
            public Action Cancelled;
            public Action Declined;
            public bool Guarded;
            public bool RefreshAssets;
            public bool ChangesRepository;
            public bool ReportsProgress;
            public bool AutoRefreshDisallowed;
        }

        private static readonly Queue<Operation> Pending = new Queue<Operation>();
        private static Operation _current;

        internal const string InterruptedKey = "Vetka.Operation.Interrupted";

        static GitOperations()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            if (Application.isBatchMode)
            {
                return;
            }

            EditorApplication.wantsToQuit += OnWantsToQuit;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnBeforeAssemblyReload()
        {
            var operation = _current;
            if (operation == null)
            {
                return;
            }

            if (operation.AutoRefreshDisallowed)
            {
                operation.AutoRefreshDisallowed = false;
                AssetDatabase.AllowAutoRefresh();
            }

            if (!operation.Guarded)
            {
                return;
            }

            SessionState.SetBool(InterruptedKey, true);
            Debug.LogWarning($"Vetka: \"{Describe(operation.Title)}\" was interrupted by a script reload, " +
                             "check the project state in the Changes tab");
        }

        internal static string GuardedTitle =>
            (_current != null && _current.Guarded ? _current : Pending.FirstOrDefault(operation => operation.Guarded))
            ?.Title;

        public static event Action RepositoryChanged;

        public static void Run(string title, Func<GitResult> work, Action<GitResult> onCompleted = null,
            bool refreshAssets = false, bool reportsProgress = false, bool changesRepository = false,
            Func<bool> prepare = null)
        {
            EditorApplication.LockReloadAssemblies();
            Pending.Enqueue(new Operation
            {
                Title = title,
                Prepare = prepare,
                Work = () => work(),
                Completed = result => onCompleted?.Invoke((GitResult)result),
                Failed = exception =>
                    onCompleted?.Invoke(GitResult.Failure($"{Describe(title)} failed: {exception.Message}")),
                Declined = () => onCompleted?.Invoke(GitResult.Cancelled($"{Describe(title)} cancelled")),
                Guarded = true,
                RefreshAssets = refreshAssets,
                ChangesRepository = changesRepository || refreshAssets,
                ReportsProgress = reportsProgress
            });

            StartNext();
        }

        public static void Read<T>(string title, Func<T> work, Action<T> onCompleted, Action<string> onAborted = null)
        {
            Pending.Enqueue(new Operation
            {
                Title = title,
                Work = () => OperationContext.Interruptible(work, out _),
                Completed = result => onCompleted?.Invoke((T)result),
                Failed = exception => onAborted?.Invoke($"Failed: {exception.Message}"),
                Cancelled = () => onAborted?.Invoke("Cancelled")
            });

            StartNext();
        }

        private static void StartNext()
        {
            while (_current == null && Pending.Count > 0)
            {
                var operation = Pending.Dequeue();
                _current = operation;
                if (operation.Prepare == null || Prepared(operation))
                {
                    Start(operation);
                    return;
                }

                _current = null;
            }
        }

        private static bool Prepared(Operation operation)
        {
            bool ready;
            try
            {
                ready = operation.Prepare();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                ready = false;
            }

            if (ready)
            {
                return true;
            }

            if (operation.Guarded)
            {
                EditorApplication.UnlockReloadAssemblies();
            }

            try
            {
                operation.Declined?.Invoke();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            return false;
        }

        private static void Start(Operation operation)
        {
            int? progressId = null;
            var context = new OperationContext(null);
            try
            {
                progressId = Progress.Start(operation.Title, null,
                    operation.ReportsProgress ? Progress.Options.None : Progress.Options.Indefinite);
                if (operation.ReportsProgress)
                {
                    Progress.Report(progressId.Value, 0f, "Starting...");
                }

                if (operation.RefreshAssets)
                {
                    AssetDatabase.DisallowAutoRefresh();
                    operation.AutoRefreshDisallowed = true;
                }

                context = new OperationContext(progressId);
                var running = context;
                var id = progressId;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    object result = null;
                    Exception failure = null;
                    try
                    {
                        result = OperationContext.With(running, operation.Work);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        EditorDispatcher.Enqueue(() => Complete(operation, running, id, result, failure));
                    }
                });
            }
            catch (Exception exception)
            {
                Complete(operation, context, progressId, null, exception);
            }
        }

        private static void Complete(Operation operation, OperationContext context, int? progressId, object result,
            Exception failure)
        {
            try
            {
                if (operation.Guarded)
                {
                    EditorApplication.UnlockReloadAssemblies();
                }

                if (operation.AutoRefreshDisallowed)
                {
                    AssetDatabase.AllowAutoRefresh();
                }

                var cancelled = failure == null && context.IsCancelled;
                if (progressId.HasValue && Progress.Exists(progressId.Value))
                {
                    Progress.Finish(progressId.Value, failure != null ? Progress.Status.Failed
                        : cancelled ? Progress.Status.Canceled
                        : Progress.Status.Succeeded);
                }

                if (cancelled && !operation.Guarded)
                {
                    Notification.Show($"{Describe(operation.Title)} cancelled");
                    operation.Cancelled?.Invoke();
                    return;
                }

                if (operation.RefreshAssets)
                {
                    AssetDatabase.Refresh();
                }

                if (failure != null)
                {
                    Debug.LogException(failure);
                    operation.Failed?.Invoke(failure);
                }
                else
                {
                    operation.Completed?.Invoke(result);
                }

                if (operation.ChangesRepository)
                {
                    RepositoryChanged?.Invoke();
                }
            }
            finally
            {
                _current = null;
                StartNext();
            }
        }

        private static string Describe(string title)
        {
            var text = title.StartsWith("Git: ") ? title.Substring("Git: ".Length) : title;
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        private static bool OnWantsToQuit()
        {
            var title = GuardedTitle;
            return title == null || !EditorUtility.DisplayDialog("Git",
                $"\"{Describe(title)}\" is still running.\n\nIf Unity quits now, the operation stops halfway. " +
                "If it saved local changes in the stash, Vetka will point to them on the next start.",
                "Wait", "Quit Anyway");
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode)
            {
                return;
            }

            var title = GuardedTitle;
            if (title == null)
            {
                return;
            }

            EditorApplication.isPlaying = false;
            EditorApplication.delayCall += () => EditorUtility.DisplayDialog("Git",
                $"Play Mode is not available while \"{Describe(title)}\" is running, try again when it finishes", "OK");
        }
    }
}
