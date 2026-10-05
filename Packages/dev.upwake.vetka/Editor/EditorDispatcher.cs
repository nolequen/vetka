using System;
using System.Collections.Concurrent;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [InitializeOnLoad]
    internal static class EditorDispatcher
    {
        private static readonly ConcurrentQueue<Action> Actions = new ConcurrentQueue<Action>();

        static EditorDispatcher()
        {
            EditorApplication.update += Update;
        }

        public static void Enqueue(Action action)
        {
            if (action != null)
            {
                Actions.Enqueue(action);
            }
        }

        private static void Update()
        {
            while (Actions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }
    }
}
