using System;
using UnityEditor;

namespace Upwake.Vetka
{
    internal sealed class OperationContext
    {
        [ThreadStatic] private static OperationContext s_current;

        private readonly object _gate = new object();
        private readonly int? _progressId;
        private int _interruptible;
        private bool _cancelled;

        public OperationContext(int? progressId)
        {
            _progressId = progressId;
        }

        public static OperationContext Current => s_current;

        public static T With<T>(OperationContext context, Func<T> run)
        {
            var previous = s_current;
            s_current = context;
            try
            {
                return run();
            }
            finally
            {
                s_current = previous;
            }
        }

        public static T Interruptible<T>(Func<T> run, out bool cancelled)
        {
            var context = s_current;
            if (context == null)
            {
                cancelled = false;
                return run();
            }

            T result;
            context.Enter();
            try
            {
                result = run();
            }
            finally
            {
                context.Exit();
            }

            cancelled = context.IsCancelled;
            return result;
        }

        public bool IsCancelled
        {
            get
            {
                lock (_gate)
                {
                    return _cancelled;
                }
            }
        }

        public bool ShouldStop
        {
            get
            {
                lock (_gate)
                {
                    return _cancelled && _interruptible > 0;
                }
            }
        }

        public Action<float, string> Reporter
        {
            get
            {
                if (!_progressId.HasValue)
                {
                    return null;
                }

                var id = _progressId.Value;
                return (progress, description) => Progress.Report(id, progress, description);
            }
        }

        public bool TryCancel()
        {
            lock (_gate)
            {
                if (_interruptible == 0)
                {
                    return false;
                }

                _cancelled = true;
                return true;
            }
        }

        private void Enter()
        {
            lock (_gate)
            {
                if (_interruptible++ > 0)
                {
                    return;
                }
            }

            SetCancellable(true);
        }

        private void Exit()
        {
            lock (_gate)
            {
                if (--_interruptible > 0)
                {
                    return;
                }
            }

            SetCancellable(false);
        }

        private void SetCancellable(bool cancellable)
        {
            if (!_progressId.HasValue)
            {
                return;
            }

            var id = _progressId.Value;
            EditorDispatcher.Enqueue(() =>
            {
                if (!Progress.Exists(id) || Progress.GetStatus(id) != Progress.Status.Running)
                {
                    return;
                }

                if (cancellable)
                {
                    Progress.RegisterCancelCallback(id, TryCancel);
                }
                else
                {
                    Progress.UnregisterCancelCallback(id);
                }
            });
        }
    }
}
