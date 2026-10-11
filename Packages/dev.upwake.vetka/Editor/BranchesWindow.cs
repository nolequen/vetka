using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class BranchesWindow : EditorWindow
    {
        private BranchesView _view;

        public static void ShowWindow()
        {
            var window = GetWindow<BranchesWindow>(utility: false, "Git branches");
            window.minSize = new Vector2(380, 300);
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            _view = new BranchesView(new Git(), Repaint);
            _view.Reload();
            GitOperations.RepositoryChanged += _view.Reload;
        }

        private void OnDisable()
        {
            GitOperations.RepositoryChanged -= _view.Reload;
        }

        private void OnFocus()
        {
            if (_view != null && _view.IsStale)
            {
                _view.Reload();
            }
        }

        private void OnGUI()
        {
            _view.OnGUI(new Rect(0, 0, position.width, position.height));
        }
    }
}
