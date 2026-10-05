using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [FilePath("UserSettings/VetkaSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal class GitProjectSettings : ScriptableSingleton<GitProjectSettings>
    {
        [SerializeField] private bool _useGlobalIdentity;
        [SerializeField] private UpdateStrategy _updateStrategy = UpdateStrategy.Ask;
        [SerializeField] private List<string> _favoriteBranches = new List<string>();

        public bool UseGlobalIdentity
        {
            get => _useGlobalIdentity;
            set
            {
                if (_useGlobalIdentity == value)
                {
                    return;
                }

                _useGlobalIdentity = value;
                Save(true);
            }
        }

        public UpdateStrategy UpdateStrategy
        {
            get => _updateStrategy;
            set
            {
                if (_updateStrategy == value)
                {
                    return;
                }

                _updateStrategy = value;
                Save(true);
            }
        }

        public static string FavoriteKey(GitBranch branch) => (branch.IsRemote ? "remote:" : "local:") + branch.Name;

        public bool IsFavorite(GitBranch branch) => _favoriteBranches.Contains(FavoriteKey(branch));

        public void ToggleFavorite(GitBranch branch)
        {
            var key = FavoriteKey(branch);
            if (!_favoriteBranches.Remove(key))
            {
                _favoriteBranches.Add(key);
                _favoriteBranches.Sort();
            }

            Save(true);
        }
    }
}
