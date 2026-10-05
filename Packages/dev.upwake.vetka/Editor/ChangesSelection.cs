using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [FilePath("UserSettings/VetkaChangesSelection.asset", FilePathAttribute.Location.ProjectFolder)]
    internal class ChangesSelection : ScriptableSingleton<ChangesSelection>
    {
        private static readonly TimeSpan Expiry = TimeSpan.FromDays(30);

        [SerializeField] private List<string> _checked = new List<string>();
        [SerializeField] private List<string> _unchecked = new List<string>();
        [SerializeField] private List<string> _missing = new List<string>();
        [SerializeField] private List<long> _missingSince = new List<long>();

        private HashSet<string> _checkedSet;
        private HashSet<string> _uncheckedSet;
        private Dictionary<string, long> _missingTimes;

        public bool IsChecked(string path, bool byDefault)
        {
            EnsureSets();
            return _checkedSet.Contains(path) || byDefault && !_uncheckedSet.Contains(path);
        }

        public void Set(IEnumerable<string> paths, bool value)
        {
            EnsureSets();
            foreach (var path in paths)
            {
                (value ? _checkedSet : _uncheckedSet).Add(path);
                (value ? _uncheckedSet : _checkedSet).Remove(path);
                _missingTimes.Remove(path);
            }

            Store();
        }

        public void Forget(IEnumerable<string> paths)
        {
            EnsureSets();
            var removed = 0;
            foreach (var path in paths)
            {
                removed += (_checkedSet.Remove(path) ? 1 : 0) + (_uncheckedSet.Remove(path) ? 1 : 0);
                _missingTimes.Remove(path);
            }

            if (removed > 0)
            {
                Store();
            }
        }

        public void Retain(ICollection<string> present, DateTime now)
        {
            EnsureSets();
            var changed = false;
            foreach (var path in _checkedSet.Concat(_uncheckedSet).ToList())
            {
                if (present.Contains(path))
                {
                    changed |= _missingTimes.Remove(path);
                }
                else if (!_missingTimes.TryGetValue(path, out var since))
                {
                    _missingTimes[path] = now.Ticks;
                    changed = true;
                }
                else if (now.Ticks - since > Expiry.Ticks)
                {
                    _checkedSet.Remove(path);
                    _uncheckedSet.Remove(path);
                    _missingTimes.Remove(path);
                    changed = true;
                }
            }

            if (changed)
            {
                Store();
            }
        }

        private void EnsureSets()
        {
            _checkedSet ??= new HashSet<string>(_checked);
            _uncheckedSet ??= new HashSet<string>(_unchecked);
            if (_missingTimes == null)
            {
                _missingTimes = new Dictionary<string, long>();
                for (var i = 0; i < _missing.Count && i < _missingSince.Count; i++)
                {
                    _missingTimes[_missing[i]] = _missingSince[i];
                }
            }
        }

        private void Store()
        {
            _checked = _checkedSet.OrderBy(path => path).ToList();
            _unchecked = _uncheckedSet.OrderBy(path => path).ToList();
            var missing = _missingTimes.OrderBy(entry => entry.Key).ToList();
            _missing = missing.Select(entry => entry.Key).ToList();
            _missingSince = missing.Select(entry => entry.Value).ToList();
            Save(true);
        }
    }
}
