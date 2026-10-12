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
            public bool ChangesRefs;
            public bool RefreshAssets;
            public bool ReportsProgress;
            public int? ProgressId;
            public bool AutoRefreshDisallowed;
        }

        private static readonly Queue<Operation> Pending = new Queue<Operation>();
        private static Operation _current;

        internal const string InterruptedKey = "Vetka.Operation.Interrupted";

        internal const string CancelledReason = "Cancelled";

        internal static int Generation { get; private set; }

        static GitOperations()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            if (Application.isBatchMode)
            {
                return;
            }

            EditorApplication.wantsToQuit += OnWantsToQuit;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.focusChanged += focused =>
            {
                if (!focused)
                {
                    Generation++;
                }
            };
            EditorApplication.projectChanged += () => Generation++;
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

        public static void Run(string title, Func<GitResult> work, Action<GitResult> onCompleted,
            bool changesTree = false, bool reportsProgress = false, Func<bool> prepare = null,
            int? progressId = null)
        {
            EditorApplication.LockReloadAssemblies();
            Pending.Enqueue(new Operation
            {
                Title = title,
                Prepare = prepare ?? (changesTree ? UnsavedChanges.SaveOrCancel : (Func<bool>)null),
                Work = () => work(),
                Completed = result => onCompleted((GitResult)result),
                Failed = exception => onCompleted(GitResult.Failure($"{Describe(title)} failed: {exception.Message}")),
                Declined = () => onCompleted(GitResult.Cancelled($"{Describe(title)} cancelled")),
                Guarded = true,
                RefreshAssets = changesTree,
                ReportsProgress = reportsProgress,
                ProgressId = progressId
            });

            StartNext();
        }

        public static int StartProgress(string title, bool reportsProgress)
        {
            var id = Progress.Start(title, null,
                reportsProgress ? Progress.Options.None : Progress.Options.Indefinite);
            if (reportsProgress)
            {
                Progress.Report(id, 0f, "Starting...");
            }

            return id;
        }

        public static void DropProgress(int id)
        {
            if (Progress.Exists(id))
            {
                Progress.Remove(id);
            }
        }

        public static void Read<T>(string title, Func<T> work, Action<T> onCompleted, Action<string> onAborted = null,
            bool changesRefs = false)
        {
            if (changesRefs)
            {
                EditorApplication.LockReloadAssemblies();
            }

            Pending.Enqueue(new Operation
            {
                Title = title,
                Work = () => OperationContext.Interruptible(work, out _),
                Completed = result => onCompleted((T)result),
                Failed = exception => onAborted?.Invoke($"Failed: {exception.Message}"),
                Cancelled = () => onAborted?.Invoke(CancelledReason),
                ChangesRefs = changesRefs
            });

            StartNext();
        }

        private static void Changed(Operation operation)
        {
            if (!operation.Guarded && !operation.ChangesRefs)
            {
                return;
            }

            Generation++;
            RepositoryChanged?.Invoke();
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

            if (operation.ProgressId.HasValue)
            {
                DropProgress(operation.ProgressId.Value);
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
                progressId = operation.ProgressId ?? StartProgress(operation.Title, operation.ReportsProgress);
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
                if (operation.Guarded || operation.ChangesRefs)
                {
                    EditorApplication.UnlockReloadAssemblies();
                }

                if (operation.AutoRefreshDisallowed)
                {
                    AssetDatabase.AllowAutoRefresh();
                }

                var cancelled = failure == null && context.IsCancelled;
                var failed = failure != null || result is GitResult { IsDone: false, IsCancelled: false };
                if (progressId.HasValue && Progress.Exists(progressId.Value))
                {
                    Progress.Finish(progressId.Value, failed ? Progress.Status.Failed
                        : cancelled ? Progress.Status.Canceled
                        : Progress.Status.Succeeded);
                }

                if (cancelled && !operation.Guarded)
                {
                    Notification.Show($"{Describe(operation.Title)} cancelled");
                    operation.Cancelled?.Invoke();
                    Changed(operation);
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

                Changed(operation);
            }
            finally
            {
                _current = null;
                StartNext();
            }
        }

        private static string Describe(string title)
        {
            return Texts.Capitalized(title.StartsWith("Git: ") ? title.Substring("Git: ".Length) : title);
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
