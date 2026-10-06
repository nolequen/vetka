using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class GitIdentityWindow : EditorWindow
    {
        private const string NameField = "GitIdentityName";
        private const string EmailField = "GitIdentityEmail";

        private string _name = "";
        private string _email = "";
        private bool _global = true;
        private Action<GitIdentity, bool> _onSubmit;
        private Action _onCancel;
        [NonSerialized] private bool _focused;
        [NonSerialized] private bool _closing;

        public static void Ensure(Git git, Action proceed, Action stopped = null)
        {
            GitOperations.Read(
                "Git: reading the user identity",
                git.Identities,
                identities =>
                {
                    var settings = GitProjectSettings.instance;
                    if (identities.Local.IsComplete || settings.UseGlobalIdentity && identities.Global.IsComplete)
                    {
                        proceed();
                        return;
                    }

                    var local = identities.Local;
                    var noLocal = string.IsNullOrEmpty(local.Name) && string.IsNullOrEmpty(local.Email);
                    var prefill = noLocal ? identities.Global : local;

                    Show(prefill, noLocal && identities.Global.IsComplete, (entered, global) =>
                        GitOperations.Run(
                            "Git: saving the user identity",
                            () => git.SetIdentity(entered, global, global ? identities.Global : local),
                            result =>
                            {
                                if (!result.IsSuccess)
                                {
                                    Notification.Show(result);
                                    stopped?.Invoke();
                                    return;
                                }

                                settings.UseGlobalIdentity = global;
                                proceed();
                            }), stopped);
                },
                reason =>
                {
                    if (reason != "Cancelled")
                    {
                        Notification.Show(GitResult.Failure($"The user identity cannot be read\n{reason}"));
                    }

                    stopped?.Invoke();
                });
        }

        public static void Show(GitIdentity current, bool global, Action<GitIdentity, bool> callback,
            Action cancelled = null)
        {
            var window = CreateInstance<GitIdentityWindow>();
            window.titleContent = new GUIContent("Git User");
            window._name = current.Name;
            window._email = current.Email;
            window._global = global;
            window._onSubmit = callback;
            window._onCancel = cancelled;

            var size = new Vector2(420, 165);
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.center - size / 2, size);
            window.minSize = size;
            window.maxSize = size;

            window.ShowUtility();
            window.Focus();
        }

        private void OnGUI()
        {
            if (_onSubmit == null)
            {
                CloseLater();
                return;
            }

            var current = Event.current;
            var submit = current.type == EventType.KeyDown &&
                         (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);
            var cancel = current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape;

            EditorGUILayout.LabelField(
                "This repository has no user name and email of its own. Choose which ones to sign commits with.",
                EditorStyles.wordWrappedLabel);

            GUI.SetNextControlName(NameField);
            _name = EditorGUILayout.TextField("Name", _name);
            GUI.SetNextControlName(EmailField);
            _email = EditorGUILayout.TextField("Email", _email);

            if (GUILayout.Toggle(_global, "Global settings, for all repositories", "Radio"))
            {
                _global = true;
            }

            if (GUILayout.Toggle(!_global, "Local settings, only for this repository", "Radio"))
            {
                _global = false;
            }

            if (!_focused)
            {
                EditorGUI.FocusTextInControl(string.IsNullOrWhiteSpace(_name) ? NameField : EmailField);
                _focused = true;
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            var identity = new GitIdentity(_name, _email);
            using (new EditorGUI.DisabledScope(!identity.IsComplete))
            {
                if (GUILayout.Button("Save and Continue", GUILayout.Width(125)) || submit && identity.IsComplete)
                {
                    var callback = _onSubmit;
                    var global = _global;
                    _onSubmit = null;
                    _onCancel = null;
                    EditorApplication.delayCall += () => callback(identity, global);
                    CloseLater();
                }
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(80)) || cancel)
            {
                Cancel();
                CloseLater();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void OnDestroy() => Cancel();

        private void Cancel()
        {
            var cancelled = _onCancel;
            _onSubmit = null;
            _onCancel = null;
            if (cancelled != null)
            {
                EditorApplication.delayCall += () => cancelled();
            }
        }

        private void CloseLater()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            EditorApplication.delayCall += () =>
            {
                if (this != null)
                {
                    Close();
                }
            };
        }
    }
}
