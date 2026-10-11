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

        [Serializable]
        private class Mark
        {
            public string Path;
            public bool Checked;
            public long MissingSince;
        }

        [SerializeField] private List<Mark> _marks = new List<Mark>();
        [SerializeField] private List<string> _checked = new List<string>();
        [SerializeField] private List<string> _unchecked = new List<string>();

        private Dictionary<string, Mark> _byPath;

        public bool IsChecked(string path, bool byDefault) =>
            Marks().TryGetValue(path, out var mark) ? mark.Checked : byDefault;

        public void Set(IEnumerable<string> paths, bool value)
        {
            var marks = Marks();
            foreach (var path in paths)
            {
                marks[path] = new Mark { Path = path, Checked = value };
            }

            Store();
        }

        public void Forget(IEnumerable<string> paths)
        {
            var marks = Marks();
            var removed = false;
            foreach (var path in paths)
            {
                removed |= marks.Remove(path);
            }

            if (removed)
            {
                Store();
            }
        }

        public void Retain(ICollection<string> present, DateTime now)
        {
            var marks = Marks();
            var changed = false;
            foreach (var mark in marks.Values.ToList())
            {
                if (present.Contains(mark.Path))
                {
                    changed |= mark.MissingSince != 0;
                    mark.MissingSince = 0;
                }
                else if (mark.MissingSince == 0)
                {
                    mark.MissingSince = now.Ticks;
                    changed = true;
                }
                else if (now.Ticks - mark.MissingSince > Expiry.Ticks)
                {
                    marks.Remove(mark.Path);
                    changed = true;
                }
            }

            if (changed)
            {
                Store();
            }
        }

        private Dictionary<string, Mark> Marks()
        {
            if (_byPath != null)
            {
                return _byPath;
            }

            _byPath = new Dictionary<string, Mark>();
            foreach (var path in _checked)
            {
                _byPath[path] = new Mark { Path = path, Checked = true };
            }

            foreach (var path in _unchecked)
            {
                _byPath[path] = new Mark { Path = path };
            }

            foreach (var mark in _marks.Where(mark => !string.IsNullOrEmpty(mark?.Path)))
            {
                _byPath[mark.Path] = mark;
            }

            return _byPath;
        }

        private void Store()
        {
            _marks = _byPath.Values.OrderBy(mark => mark.Path).ToList();
            _checked.Clear();
            _unchecked.Clear();
            Save(true);
        }
    }
}
