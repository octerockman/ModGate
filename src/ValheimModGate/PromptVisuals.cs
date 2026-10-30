using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private static readonly Color Ink = new Color(.933f, .918f, .871f, 1);
        private static readonly Color Muted = new Color(.659f, .702f, .671f, 1);
        private static readonly Color Gold = new Color(.875f, .741f, .467f, 1);
        private static readonly Color Green = new Color(.604f, .812f, .667f, 1);
        private static readonly Color Yellow = new Color(.914f, .780f, .490f, 1);
        private static readonly Color Red = new Color(.945f, .631f, .596f, 1);
        private static readonly Color Panel = new Color(.102f, .133f, .129f, 1);
        private static readonly Color Line = new Color(.208f, .255f, .235f, 1);
        private readonly List<Texture2D> visualTextures = new List<Texture2D>();
        private readonly Dictionary<string, GUIStyle> visualLabels = new Dictionary<string, GUIStyle>();
        private readonly Dictionary<string, GUIStyle> visualButtons = new Dictionary<string, GUIStyle>();
        private GUIStyle? cardBox, emptyBox, invisibleControl, inputStyle, checkBox, checkOn, badgeBox;
        private Font? displayFont;

        private void InitializeVisuals()
        {
            if (cardBox != null) return;
            try { displayFont = Font.CreateDynamicFontFromOSFont(new[] { "Georgia", "Liberation Serif", "DejaVu Serif" }, 28); } catch { }
            cardBox = BoxStyle(Panel, Line, 10);
            emptyBox = BoxStyle(new Color(.067f, .09f, .09f, 1), Line, 10);
            badgeBox = BoxStyle(Panel, Panel, 5);
            checkBox = BoxStyle(new Color(.067f, .09f, .09f, 1), new Color(.43f, .49f, .45f, 1), 4);
            checkOn = BoxStyle(Gold, Gold, 4);
            invisibleControl = new GUIStyle { padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), border = new RectOffset(0, 0, 0, 0) };
            foreach (var state in new[] { invisibleControl.normal, invisibleControl.hover, invisibleControl.active, invisibleControl.focused, invisibleControl.onNormal, invisibleControl.onHover, invisibleControl.onActive, invisibleControl.onFocused }) state.textColor = new Color(1, 1, 1, 0);
            inputStyle = BoxStyle(new Color(.067f, .09f, .09f, 1), Line, 8);
            inputStyle.font = body!.font; inputStyle.fontSize = 14; inputStyle.alignment = TextAnchor.MiddleLeft;
            inputStyle.padding = new RectOffset(13, 13, 7, 7); inputStyle.normal.textColor = Ink;
            inputStyle.focused.background = Rounded(Panel, Gold, 8); inputStyle.focused.textColor = Ink;
            skin!.textField = new GUIStyle(inputStyle) { fixedHeight = 38 };
            skin.textArea = new GUIStyle(inputStyle) { wordWrap = true, alignment = TextAnchor.UpperLeft, fixedHeight = 0 };
            button = ButtonStyle("secondary", false);
            primaryStyle = ButtonStyle("primary", false);
            skin.button = button;
            skin.toggle = new GUIStyle(body!) { fixedHeight = 32 };
        }
        private GUIStyle BoxStyle(Color fill, Color stroke, int radius)
        {
            var slice = radius + 1;
            var style = new GUIStyle { border = new RectOffset(slice, slice, slice, slice), padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            style.normal.background = Rounded(fill, stroke, radius); return style;
        }
        private Texture2D Rounded(Color fill, Color stroke, int radius)
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
            {
                var px = x + .5f; var py = y + .5f;
                var dx = Mathf.Max(radius - px, Mathf.Max(px - (size - radius), 0));
                var dy = Mathf.Max(radius - py, Mathf.Max(py - (size - radius), 0));
                var edge = (float)Math.Sqrt(dx * dx + dy * dy) - radius;
                var outer = Mathf.Clamp(.5f - edge, 0, 1);
                var inner = x > 0 && x < size - 1 && y > 0 && y < size - 1 && edge < -1;
                var color = inner ? fill : stroke;
                texture.SetPixel(x, y, new Color(color.r, color.g, color.b, color.a * outer));
            }
            texture.Apply(); visualTextures.Add(texture); return texture;
        }
        private static Color Tone(string tone) => tone == "muted" ? Muted : tone == "gold" ? Gold : tone == "green" ? Green : tone == "yellow" ? Yellow : tone == "red" ? Red : Ink;
        private GUIStyle TextStyle(string kind, string tone)
        {
            var key = kind + "/" + tone;
            if (visualLabels.TryGetValue(key, out var style)) return style;
            style = new GUIStyle(body!) { fontSize = kind == "title" ? 29 : kind == "heading" ? 18 : kind == "small" ? 12 : 14,
                padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), alignment = TextAnchor.MiddleLeft,
                wordWrap = false, clipping = TextClipping.Clip, richText = false, contentOffset = Vector2.zero };
            if (kind == "title" && displayFont != null) style.font = displayFont;
            style.normal.textColor = Tone(tone); visualLabels[key] = style; return style;
        }
        private GUIStyle ButtonStyle(string kind, bool selected)
        {
            var key = kind + "/" + selected;
            if (visualButtons.TryGetValue(key, out var style)) return style;
            var primary = kind == "primary";
            var quiet = kind == "quiet" || kind == "link" || kind.StartsWith("link-", StringComparison.Ordinal) || kind == "tab" || kind == "nav" && !selected;
            var fill = primary ? Gold : selected ? new Color(.212f, .192f, .137f, 1) : Panel;
            var stroke = primary ? Gold : selected ? new Color(.37f, .32f, .21f, 1) : Line;
            style = quiet ? new GUIStyle() : BoxStyle(fill, stroke, 8);
            style.fontSize = 14; style.padding = new RectOffset(14, 14, 9, 9); style.margin = new RectOffset(0, 0, 0, 0);
            style.wordWrap = false; style.richText = false; style.fixedHeight = 38; style.alignment = TextAnchor.MiddleCenter;
            if (quiet) style.border = new RectOffset(12, 12, 12, 12);
            style.hover.background = Rounded(primary ? new Color(.93f, .81f, .55f, 1) : new Color(.149f, .192f, .169f, 1), primary ? Gold : Line, 8);
            style.active.background = style.hover.background; style.focused.background = style.hover.background;
            // Text is painted separately so locked controls do not dim informational labels.
            foreach (var state in new[] { style.normal, style.hover, style.active, style.focused, style.onNormal, style.onHover, style.onActive, style.onFocused }) state.textColor = new Color(1, 1, 1, 0);
            visualButtons[key] = style; return style;
        }
        private Rect Reserve(float height)
        {
            var area = GUILayoutUtility.GetRect(1, height, GUILayout.ExpandWidth(true));
            return new Rect(area.x, area.y, Mathf.Max(1, contentWidth - 20), height);
        }
        private void Card(Rect area, bool empty = false) { InitializeVisuals(); GUI.Box(area, "", empty ? emptyBox! : cardBox!); }
        private void Divider(Rect area) { GUI.DrawTexture(new Rect(area.x, area.y, area.width, 1), separatorBackground!); }
        private void LabelAt(Rect area, string text, string kind = "body", string tone = "ink")
        {
            InitializeVisuals(); var enabled = GUI.enabled; GUI.enabled = true;
            try { GUI.Label(area, text, TextStyle(kind, tone)); } finally { GUI.enabled = enabled; }
        }
        private bool Action(Rect area, string label, string kind = "secondary", bool selected = false)
        {
            InitializeVisuals(); var enabled = GUI.enabled; var style = ButtonStyle(kind, selected);
            var clicked = GUI.Button(area, label, style); if (clicked) { pointerClick = null; pointerPress = null; }
            else clicked = PointerClicked(area);
            var display = Regex.Replace(label, @"\s+\((offline|server-[1-4])\)$", "");
            if (Regex.IsMatch(display, @"^Delete server-[1-4]$")) display = "Delete profile…";
            if (display.StartsWith("View: ", StringComparison.Ordinal)) display = display.Substring(6);
            if (display.EndsWith(" •", StringComparison.Ordinal)) display = display.Substring(0, display.Length - 2);
            var tone = kind == "primary" ? "dark" : !enabled ? "muted" : selected ? "gold" : kind.StartsWith("link-", StringComparison.Ordinal) ? kind.Substring(5) : "ink";
            var labelStyle = TextStyle("body", tone);
            var old = labelStyle.alignment;
            labelStyle.alignment = kind == "nav" || kind.StartsWith("link", StringComparison.Ordinal) ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
            if (tone == "dark") labelStyle.normal.textColor = new Color(.114f, .129f, .102f, 1);
            GUI.enabled = true;
            GUI.Label(new Rect(area.x + 12, area.y, Mathf.Max(1, area.width - 24), area.height), display, labelStyle);
            labelStyle.alignment = old; GUI.enabled = enabled;
            if (kind == "tab" && selected) GUI.DrawTexture(new Rect(area.x + 12, area.y + area.height - 2, area.width - 24, 2), goldBackground!);
            return clicked;
        }
        private bool Check(Rect area, bool selected, bool enabled, string label)
        {
            InitializeVisuals(); var before = GUI.enabled; GUI.enabled = enabled && before;
            var next = GUI.Toggle(area, selected, label, invisibleControl!);
            if (next != selected) { pointerClick = null; pointerPress = null; }
            else if (PointerClicked(area)) next = !selected;
            GUI.enabled = true; GUI.Box(area, "", selected ? checkOn! : checkBox!);
            if (selected)
            {
                var check = TextStyle("body", "check"); check.alignment = TextAnchor.MiddleCenter; check.normal.textColor = new Color(.114f, .129f, .102f, 1);
                GUI.Label(area, "✓", check);
            }
            GUI.enabled = before; return next;
        }
        private void Badge(Rect area, string label, string tone)
        {
            InitializeVisuals(); var key = "badge/" + tone;
            if (!visualButtons.TryGetValue(key, out var style))
            {
                var fill = tone == "green" ? new Color(.141f, .227f, .173f, 1) : tone == "red" ? new Color(.251f, .169f, .161f, 1) : tone == "yellow" || tone == "gold" ? new Color(.235f, .204f, .141f, 1) : Panel;
                style = BoxStyle(fill, fill, 5); visualButtons[key] = style;
            }
            var enabled = GUI.enabled; GUI.enabled = true; GUI.Box(area, "", style);
            LabelAt(new Rect(area.x + 9, area.y, area.width - 18, area.height), label, "small", tone); GUI.enabled = enabled;
        }
        private string TextInput(Rect area, string value, string control)
        {
            InitializeVisuals(); GUI.SetNextControlName(control);
            var result = GUI.TextField(area, value, inputStyle!);
            if (PointerClicked(area)) GUI.FocusControl(control); return result;
        }
        private void Section(string titleText, string subtitle = "")
        {
            GUILayout.Space(14); var area = Reserve(subtitle.Length == 0 ? 32 : 54);
            LabelAt(new Rect(area.x, area.y, area.width, 28), titleText, "heading");
            if (subtitle.Length > 0) LabelAt(new Rect(area.x, area.y + 29, area.width, 20), subtitle, "small", "muted");
        }
        private void Notice(string titleText, string description, string tone)
        {
            var area = Reserve(description.Length == 0 ? 54 : 76); Badge(new Rect(area.x, area.y, area.width, area.height - 8), "", tone);
            LabelAt(new Rect(area.x + 17, area.y + 9, area.width - 34, 25), titleText, "body", tone);
            if (description.Length > 0) LabelAt(new Rect(area.x + 17, area.y + 36, area.width - 34, 25), description, "small");
        }
    }
}
