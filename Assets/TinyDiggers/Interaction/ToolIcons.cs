using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>The icons on the sheet, in the order Art/Tools/td_tool_icons.py draws them. Add at the end only.</summary>
    public enum ToolIcon
    {
        Select,
        Dig,
        Fill,
        Level,
        Road,
        DumpZone,
        Clear,
        Seed,
        Settings,
        Debug,
        Undo,
        Redo,
        Eyedropper,
        Warning,
        Digger,
        Hauler,
        Worker,
        Quarry,
        Terraform,
    }

    /// <summary>
    /// The toolbar and crew panel icons (Slice 17): one sprite sheet, Resources/ToolIcons.png, of
    /// white glyphs 64 px a cell in an 8 x 8 grid, tinted in the UI. Sprites are cut from it once.
    /// </summary>
    public static class ToolIcons
    {
        public const int Cell = 64;
        public const int Grid = 8;

        static readonly Dictionary<ToolIcon, Sprite> Cache = new Dictionary<ToolIcon, Sprite>();
        static Texture2D _sheet;

        public static Sprite Get(ToolIcon icon)
        {
            if (Cache.TryGetValue(icon, out var sprite) && sprite != null)
                return sprite;
            if (_sheet == null)
                _sheet = Resources.Load<Texture2D>("ToolIcons");
            if (_sheet == null)
                return null;

            var index = (int)icon;
            var row = index / Grid;
            var column = index % Grid;
            // The sheet is drawn top row first; a texture's origin is its bottom left.
            var rect = new Rect(column * Cell, (Grid - 1 - row) * Cell, Cell, Cell);
            sprite = Sprite.Create(_sheet, rect, new Vector2(0.5f, 0.5f), 100f);
            sprite.name = icon.ToString();
            sprite.hideFlags = HideFlags.DontSave;
            Cache[icon] = sprite;
            return sprite;
        }
    }
}
