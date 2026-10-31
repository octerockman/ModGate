// Small external-engine doubles. The tests execute the production Prompt.cs unchanged.
using UnityEngine;

namespace UnityEngine
{
    public enum CursorLockMode { None, Locked, Confined }
    public enum KeyCode { F6, F8, LeftShift, RightShift, Escape, Mouse0 }
    public enum EventType { Repaint, Layout, KeyDown, Used }
    public enum TextureFormat { RGBA32 }
    public enum FontStyle { Normal, Bold, Italic, BoldAndItalic }
    public enum TextAnchor { UpperLeft, UpperCenter, UpperRight, MiddleLeft, MiddleCenter, MiddleRight, LowerLeft, LowerCenter, LowerRight }
    public enum TextClipping { Overflow, Clip }
    public static class Cursor { public static CursorLockMode lockState; public static bool visible; }
    public static class Application { public static bool isBatchMode; public static string LastUrl = ""; public static void OpenURL(string url) { LastUrl = url; } }
    public static class Screen { public static int width = 1920, height = 1080; }
    public readonly record struct Color(float r, float g, float b, float a)
    {
        public Color(float r, float g, float b) : this(r, g, b, 1) { }
        public static Color white => new(1, 1, 1, 1);
        public static Color clear => new(0, 0, 0, 0);
    }
    public readonly record struct Matrix4x4(int Id) { public static Matrix4x4 identity => new(1); }
    public readonly record struct Vector2(float x, float y) { public static Vector2 zero => new(0, 0); }
    public readonly record struct Vector3(float x, float y, float z);
    public record struct Rect(float x, float y, float width, float height)
    {
        public bool Contains(Vector2 point) => point.x >= x && point.y >= y && point.x < x + width && point.y < y + height;
        public float xMin { get => x; set { width += x - value; x = value; } }
        public float xMax { get => x + width; set => width = value - x; }
        public float yMin { get => y; set { height += y - value; y = value; } }
        public float yMax { get => y + height; set => height = value - y; }
    }
    public sealed class RectOffset
    {
        public int left, right, top, bottom;
        public int horizontal => left + right; public int vertical => top + bottom;
        public RectOffset() { }
        public RectOffset(int left, int right, int top, int bottom) { this.left = left; this.right = right; this.top = top; this.bottom = bottom; }
    }
    public class Object
    {
        public static T Instantiate<T>(T item) where T : Object => item is GUISkin original ? (T)(Object)new GUISkin(original) : item;
        public static void Destroy(Object item) { }
    }
    public sealed class Texture2D : Object
    {
        public int width, height; internal Color[] Pixels;
        public Texture2D(int width, int height, TextureFormat format, bool mipChain) { this.width = width; this.height = height; Pixels = new Color[width * height]; }
        public Texture2D(int width, int height) : this(width, height, TextureFormat.RGBA32, false) { }
        public void SetPixel(int x, int y, Color color) { Pixels[y * width + x] = color; }
        public void SetPixels(Color[] colors) { Array.Copy(colors, Pixels, Pixels.Length); }
        public Color[] GetPixels() => Pixels.ToArray();
        public void Apply() { }
    }
    public static class ImageConversion
    {
        public static bool Reject;
        public static bool LoadImage(Texture2D texture, byte[] bytes, bool markNonReadable)
        {
            if (Reject || bytes.Length < 33) return false;
            texture.width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            texture.height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            texture.Pixels = Enumerable.Repeat(new Color(.35f,.5f,.4f,1),texture.width * texture.height).ToArray(); return true;
        }
    }
    public sealed class Font : Object
    {
        public string name = "Segoe UI";
        public static Font CreateDynamicFontFromOSFont(string[] names, int size) => new() { name = names.FirstOrDefault() ?? "Segoe UI" };
        public static Font CreateDynamicFontFromOSFont(string name, int size) => new() { name = name };
        public static string[] GetOSInstalledFontNames() => new[] { "Segoe UI", "Arial", "Tahoma" };
    }
    public sealed class GUIContent { public string text; public GUIContent(string text) { this.text = text; } public static GUIContent none => new(""); }
    public sealed class GUIStyleState { public Color textColor = Color.white; public Texture2D? background; }
    public sealed class GUIStyle
    {
        public int fontSize; public bool wordWrap, richText, stretchWidth, stretchHeight;
        public Font? font; public FontStyle fontStyle; public TextAnchor alignment; public TextClipping clipping;
        public Vector2 contentOffset; public float fixedHeight, fixedWidth;
        public RectOffset border = new(), padding = new(), margin = new();
        public GUIStyleState normal = new(), onNormal = new(), hover = new(), active = new(), focused = new(), onHover = new(), onActive = new(), onFocused = new();
        public GUIStyle() { }
        public GUIStyle(GUIStyle original)
        {
            fontSize = original.fontSize; wordWrap = original.wordWrap; richText = original.richText; font = original.font; fontStyle = original.fontStyle;
            alignment = original.alignment; clipping = original.clipping; contentOffset = original.contentOffset; fixedHeight = original.fixedHeight; fixedWidth = original.fixedWidth;
            stretchWidth = original.stretchWidth; stretchHeight = original.stretchHeight;
            border = Copy(original.border); padding = Copy(original.padding); margin = Copy(original.margin);
            var source = new[] { original.normal, original.onNormal, original.hover, original.active, original.focused, original.onHover, original.onActive, original.onFocused };
            var target = new[] { normal, onNormal, hover, active, focused, onHover, onActive, onFocused };
            for (var i = 0; i < source.Length; i++) { target[i].textColor = source[i].textColor; target[i].background = source[i].background; }
        }
        private static RectOffset Copy(RectOffset value) => new(value.left, value.right, value.top, value.bottom);
        public float lineHeight => (fontSize == 0 ? 14 : fontSize) * 1.25f;
        public Vector2 CalcSize(GUIContent content) => new(content.text.Length * (fontSize == 0 ? 14 : fontSize) * .53f + padding.horizontal, lineHeight + padding.vertical);
        public float CalcHeight(GUIContent content, float width) => wordWrap ? Math.Max(1, (float)Math.Ceiling(CalcSize(content).x / Math.Max(1, width))) * lineHeight + padding.vertical : lineHeight + padding.vertical;
    }
    public sealed class GUISkin : Object
    {
        public Font? font; public GUIStyle label = new(), button = new(), window = new(), toggle = new(), textField = new(), textArea = new();
        public GUISkin() { }
        public GUISkin(GUISkin original) { font = original.font; label = new(original.label); button = new(original.button); window = new(original.window); toggle = new(original.toggle); textField = new(original.textField); textArea = new(original.textArea); }
    }
    public sealed class ExitGUIException : Exception { }
    public sealed class Event
    {
        public static Event current = new(); public EventType type; public KeyCode keyCode; public Vector2 mousePosition;
        public void Use() => type = EventType.Used;
    }
    public static class Mathf
    {
        public static float Min(float a, float b) => Math.Min(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
    }
    public static class GUIUtility
    {
        public static string systemCopyBuffer = "";
        public static int hotControl, keyboardControl;
        public static Rect GUIToScreenRect(Rect area)
        {
            return DrawCapture.Absolute(area);
        }
    }
    public static class GUI
    {
        public delegate void WindowFunction(int id);
        public static void Box(Rect rect, string text) => Box(rect, text, skin.window);
        public static void Box(Rect rect, string text, GUIStyle style) => DrawCapture.Add("box", rect, text, style);
        public static void SetNextControlName(string name) { }
        public static void FocusControl(string name) { }
        public static void DrawTexture(Rect rect, Texture2D texture) => DrawCapture.Add("texture", rect, "", null, texture);
        public static bool Toggle(Rect area, bool selected, string text, GUIStyle style)
        {
            GUILayout.Toggles.Add((text, selected, enabled, style.normal.textColor));
            DrawCapture.Add("toggle", area, text, style, selected: selected);
            return RecordClick(area, text) ? !selected : selected;
        }
        public static GUISkin skin = new(); public static Matrix4x4 matrix = Matrix4x4.identity;
        public static Color color = Color.white, backgroundColor = Color.white, contentColor = Color.white;
        public static bool enabled = true; public static int depth;
        public readonly record struct ButtonRecord(string Text, Rect Area, bool Enabled, bool InScroll);
        public static readonly List<ButtonRecord> Buttons = new();
        public static string? Click;
        public static Rect? currentWindow; public static Rect LastWindow;
        public static Rect ModalWindow(int id, Rect rectangle, WindowFunction draw, string text, GUIStyle style)
        {
            currentWindow = rectangle; LastWindow = rectangle;
            LayoutRecorder.Reset();
            DrawCapture.Add("window", new Rect(0, 0, rectangle.width, rectangle.height), text, style);
            try { draw(id); } finally { currentWindow = null; LayoutRecorder.Reset(); }
            return rectangle;
        }
        public static bool Button(Rect area, string text, GUIStyle style)
        {
            DrawCapture.Add("button", area, text, style);
            return RecordClick(area, text);
        }
        private static bool RecordClick(Rect area, string text)
        {
            Buttons.Add(new(text, DrawCapture.Absolute(area), enabled, GUILayout.ScrollDepth > 0));
            if (!enabled || Click != text) return false;
            Click = null; return true;
        }
        public static void Label(Rect rectangle, string text, GUIStyle style) { GUILayout.Labels.Add(text); DrawCapture.Add("label", rectangle, text, style); }
        public static string TextField(Rect area, string value, GUIStyle style) { DrawCapture.Add("text-field", area, value, style); return value; }
        public static string TextArea(Rect area, string value, GUIStyle style) { DrawCapture.Add("text-area", area, value, style); return value; }
        public static string PasswordField(Rect area, string value, char mask, GUIStyle style) { DrawCapture.Add("password", area, new string(mask, value.Length), style); return value; }
    }
    public sealed class GUILayoutOption { internal string Kind = ""; internal float Value; }
    public static class GUILayoutUtility
    {
        public static Rect GetLastRect() => LayoutRecorder.Last;
        public static Rect GetRect(float width, float height, params GUILayoutOption[] options) => LayoutRecorder.Reserve(width, height, options);
    }
    public static class GUILayout
    {
        public static int ScrollDepth, AreaDepth;
        public static bool ThrowContent; public static readonly List<string> Labels = new();
        public static void BeginArea(Rect area) { AreaDepth++; LayoutRecorder.BeginArea(area); }
        public static void EndArea() { AreaDepth--; LayoutRecorder.EndArea(); }
        public static Vector2 BeginScrollView(Vector2 value, params GUILayoutOption[] options) { ScrollDepth++; LayoutRecorder.BeginScroll(value, options); return value; }
        public static void EndScrollView() { ScrollDepth--; LayoutRecorder.EndScroll(); }
        public static void Label(string text, GUIStyle style, params GUILayoutOption[] options) { if (ThrowContent) throw new InvalidOperationException("simulated content/layout failure"); GUI.Label(LayoutRecorder.Control(text, style, options), text, style); }
        public static void Space(float value) => LayoutRecorder.Space(value);
        public static readonly List<(string Text, bool Selected, bool Enabled, Color Color)> Toggles = new();
        public static bool Toggle(bool selected, string text, params GUILayoutOption[] options) => Toggle(selected, text, GUI.skin.toggle);
        public static bool Toggle(bool selected, string text, GUIStyle style, params GUILayoutOption[] options)
        {
            return GUI.Toggle(LayoutRecorder.Control(text, style, options), selected, text, style);
        }
        public static bool Button(string text, params GUILayoutOption[] options) => GUI.Button(LayoutRecorder.Control(text, GUI.skin.button, options), text, GUI.skin.button);
        public static string TextField(string value) => GUI.TextField(LayoutRecorder.Control(value, GUI.skin.textField, Array.Empty<GUILayoutOption>()), value, GUI.skin.textField);
        public static string TextArea(string value) => GUI.TextArea(LayoutRecorder.Control(value, GUI.skin.textArea, Array.Empty<GUILayoutOption>()), value, GUI.skin.textArea);
        public static string PasswordField(string value, char mask) => GUI.PasswordField(LayoutRecorder.Control(value, GUI.skin.textField, Array.Empty<GUILayoutOption>()), value, mask, GUI.skin.textField);
        public static void BeginVertical(GUIStyle style, params GUILayoutOption[] options) => LayoutRecorder.BeginGroup(false, style, options);
        public static void BeginVertical(params GUILayoutOption[] options) => LayoutRecorder.BeginGroup(false, null, options);
        public static void EndVertical() => LayoutRecorder.EndGroup();
        public static void BeginHorizontal(params GUILayoutOption[] options) => LayoutRecorder.BeginGroup(true, null, options);
        public static void EndHorizontal() => LayoutRecorder.EndGroup();
        public static GUILayoutOption Width(float value) => new() { Kind = "width", Value = value };
        public static GUILayoutOption Height(float value) => new() { Kind = "height", Value = value };
        public static GUILayoutOption ExpandWidth(bool value) => new() { Kind = "expand-width", Value = value ? 1 : 0 };
        public static GUILayoutOption ExpandHeight(bool value) => new() { Kind = "expand-height", Value = value ? 1 : 0 };
    }
}
public static class ZInput
{
    public static object? instance = new();
    public static readonly HashSet<KeyCode> Pressed = new();
    public static readonly HashSet<KeyCode> Released = new();
    public static readonly HashSet<KeyCode> Held = new();
    public static Action<string, KeyCode>? BeforeRead;
    public static Vector3 pointerPosition;
    public static float Wheel;
    public static bool GetKeyDown(KeyCode key) { BeforeRead?.Invoke(nameof(GetKeyDown), key); return Pressed.Remove(key); }
    public static bool GetKeyUp(KeyCode key) { BeforeRead?.Invoke(nameof(GetKeyUp), key); return Released.Remove(key); }
    public static bool GetKey(KeyCode key) { BeforeRead?.Invoke(nameof(GetKey), key); return Held.Contains(key); }
    public static void ManagerShortcut(KeyCode shift = KeyCode.LeftShift) { Held.Add(shift); Pressed.Add(KeyCode.F6); }
    public static float GetMouseScrollWheel() { var value = Wheel; Wheel = 0; return value; }
}
public static class ZCursor
{
    public static void Show() { if (ZInput.instance == null) throw new Exception("input is unavailable"); Cursor.visible = false; }
}
public sealed class ZNet { public static ZNet? instance = null; }
namespace UnityEngine.EventSystems
{
    public sealed class BaseInputModule { public bool enabled = true; }
    public sealed class EventSystem { public static EventSystem? current; public bool enabled = true; public BaseInputModule currentInputModule = new(); }
}
namespace UnityEngine.InputSystem
{
    public sealed class Mouse
    {
        public static Mouse? current = new();
        public readonly PointerControl position = new();
        public readonly ScrollControl scroll = new();
    }
    public sealed class PointerControl { public Vector2 ReadValue() => new(ZInput.pointerPosition.x, ZInput.pointerPosition.y); }
    public sealed class ScrollControl
    {
        public Vector2 ReadValue() { var wheel = ZInput.Wheel; ZInput.Wheel = 0; return new Vector2(0, wheel); }
    }
}
namespace BepInEx
{
    public static class Paths { public static string BepInExRootPath = Path.Combine(Path.GetTempPath(), "ModGate-ui-tests-" + Guid.NewGuid().ToString("N")); }
}
namespace ModGate
{
    internal static class Inventory
    {
        public static List<ModEntry> Current = new();
        public static List<ProfileEditGroup> Groups = new();
        public static List<ModEntry> Loaded() => Current;
        public static List<ProfileEditGroup> ProfileGroups(string root, ProfileCatalog catalog, string slot) => Json.CloneLocal(Groups);
        public static List<ModEntry> InspectProfilePayload(string path) => new();
        public static Func<string, List<ModEntry>>? SupportInspect = null;
        public static List<ModEntry> InspectSupportPayload(string path) => SupportInspect?.Invoke(path) ?? new();
        public static void SaveConfigFiles() { }
        public static List<ModEntry> InspectStaged(string stage) => new();
        public static List<ModEntry> CompleteProfileProposal(string root, Policy policy, PendingInstall install, bool allowIncomplete = false, Action<string>? warn = null) => new();
        public static void FilterPartialStage(string root, Policy policy, PendingInstall pending, ISet<string> selected, List<string> issues) { }
    }
}
