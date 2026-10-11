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
        private (GitIdentity Identity, bool Global)? _answer;
        [NonSerialized] private bool _focused;

        public static void Ensure(Git git, Action proceed, Action stopped = null)
        {
            GitOperations.Read(
                "Git: reading the user identity",
                git.Identities,
                identities =>
                {
                    if (!identities.Result.IsSuccess)
                    {
                        Notification.Show(
                            GitResult.Failure($"The user identity cannot be read\n{identities.Result.Message}"));
                        stopped?.Invoke();
                        return;
                    }

                    var settings = GitProjectSettings.instance;
                    if (identities.Local.IsComplete || settings.UseGlobalIdentity && identities.Global.IsComplete)
                    {
                        proceed();
                        return;
                    }

                    var local = identities.Local;
                    var noLocal = string.IsNullOrEmpty(local.Name) && string.IsNullOrEmpty(local.Email);
                    var prefill = noLocal ? identities.Global : local;

                    EditorApplication.delayCall += () =>
                    {
                        var answer = Ask(prefill, noLocal && identities.Global.IsComplete);
                        if (answer == null)
                        {
                            stopped?.Invoke();
                            return;
                        }

                        var (entered, global) = answer.Value;
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
                            });
                    };
                },
                reason =>
                {
                    if (reason != GitOperations.CancelledReason)
                    {
                        Notification.Show(GitResult.Failure($"The user identity cannot be read\n{reason}"));
                    }

                    stopped?.Invoke();
                });
        }

        private static (GitIdentity Identity, bool Global)? Ask(GitIdentity current, bool global)
        {
            var window = CreateInstance<GitIdentityWindow>();
            window._name = current.Name;
            window._email = current.Email;
            window._global = global;
            return ModalDialog.Show(window, "Git User", new Vector2(420, 165)) ? window._answer : null;
        }

        private void OnGUI()
        {
            var current = Event.current;
            var submit = ModalDialog.Submitted(current);
            var cancel = ModalDialog.Cancelled(current);

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
                    _answer = (identity, _global);
                    ModalDialog.Close(this);
                }
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(80)) || cancel)
            {
                ModalDialog.Close(this);
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
