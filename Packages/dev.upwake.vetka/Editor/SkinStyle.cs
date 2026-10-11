using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal sealed class SkinStyle
    {
        private readonly Func<GUIStyle> _create;
        private GUIStyle _style;
        private bool _proSkin;

        public SkinStyle(Func<GUIStyle> create) => _create = create;

        public GUIStyle Style
        {
            get
            {
                if (_style == null || _proSkin != EditorGUIUtility.isProSkin)
                {
                    _proSkin = EditorGUIUtility.isProSkin;
                    _style = _create();
                }

                return _style;
            }
        }

        public static implicit operator GUIStyle(SkinStyle style) => style.Style;
    }
}
