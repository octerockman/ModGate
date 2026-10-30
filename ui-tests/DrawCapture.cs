using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;

namespace UnityEngine;

internal sealed class DrawCommand
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public Rect Area { get; set; }
    public Rect Clip { get; set; }
    public Color TextColor { get; set; }
    public Color Tint { get; set; }
    public int FontSize { get; set; }
    public string Font { get; set; } = "Segoe UI";
    public FontStyle FontStyle { get; set; }
    public TextAnchor Alignment { get; set; }
    public bool Wrap { get; set; }
    public bool Enabled { get; set; }
    public bool Selected { get; set; }
    public int[] Padding { get; set; } = new int[4];
    public int[] Border { get; set; } = new int[4];
    public Vector2 ContentOffset { get; set; }
    public int TextureWidth { get; set; }
    public int TextureHeight { get; set; }
    public string? TexturePng { get; set; }
    [JsonIgnore] internal Texture2D? Texture;
}

internal static class DrawCapture
{
    public static bool Enabled;
    public static readonly List<DrawCommand> Commands = new();
    public static void Clear() { Commands.Clear(); LayoutRecorder.Reset(); }
    public static Rect Absolute(Rect rect)
    {
        var window = GUI.currentWindow ?? new Rect(); var offset = LayoutRecorder.Offset;
        return new Rect(window.x + offset.x + rect.x, window.y + offset.y + rect.y, rect.width, rect.height);
    }
    public static void Add(string kind, Rect area, string text, GUIStyle? style = null, Texture2D? texture = null, bool selected = false)
    {
        if (!Enabled) return;
        var state = style == null ? null : selected ? style.onNormal : style.normal;
        texture ??= state?.background;
        Commands.Add(new DrawCommand
        {
            Kind = kind, Text = text, Area = Absolute(area), Clip = LayoutRecorder.Clip, FontSize = style?.fontSize > 0 ? style.fontSize : 14,
            Font = style?.font?.name ?? GUI.skin.font?.name ?? "Segoe UI", FontStyle = style?.fontStyle ?? FontStyle.Normal,
            Alignment = style?.alignment ?? TextAnchor.UpperLeft, Wrap = style?.wordWrap ?? false, Enabled = GUI.enabled, Selected = selected,
            TextColor = state?.textColor ?? Color.clear, Tint = GUI.color,
            Padding = style == null ? new int[4] : new[] { style.padding.left, style.padding.right, style.padding.top, style.padding.bottom },
            Border = style == null ? new int[4] : new[] { style.border.left, style.border.right, style.border.top, style.border.bottom },
            ContentOffset = style?.contentOffset ?? Vector2.zero, Texture = texture, TextureWidth = texture?.width ?? 0, TextureHeight = texture?.height ?? 0
        });
    }
    public static void EncodeTextures()
    {
        var cache = new Dictionary<Texture2D, string>();
        foreach (var command in Commands.Where(c => c.Texture != null))
        {
            var texture = command.Texture!;
            if (!cache.TryGetValue(texture, out var png)) { png = Convert.ToBase64String(Png(texture)); cache[texture] = png; }
            command.TexturePng = png;
        }
    }
    private static byte[] Png(Texture2D texture)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; PutInt(header, 0, texture.width); PutInt(header, 4, texture.height); header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        using var packed = new MemoryStream();
        using (var zipper = new ZLibStream(packed, CompressionLevel.SmallestSize, true))
            for (var y = texture.height - 1; y >= 0; y--)
            {
                zipper.WriteByte(0);
                for (var x = 0; x < texture.width; x++)
                {
                    var color = texture.Pixels[y * texture.width + x];
                    zipper.WriteByte(Channel(color.r)); zipper.WriteByte(Channel(color.g)); zipper.WriteByte(Channel(color.b)); zipper.WriteByte(Channel(color.a));
                }
            }
        Chunk(output, "IDAT", packed.ToArray()); Chunk(output, "IEND", Array.Empty<byte>()); return output.ToArray();
    }
    private static byte Channel(float value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
    private static void PutInt(byte[] target, int offset, int value) { target[offset] = (byte)(value >> 24); target[offset + 1] = (byte)(value >> 16); target[offset + 2] = (byte)(value >> 8); target[offset + 3] = (byte)value; }
    private static void Chunk(Stream output, string type, byte[] bytes)
    {
        var length = new byte[4]; PutInt(length, 0, bytes.Length); output.Write(length);
        var name = Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(bytes);
        uint crc = uint.MaxValue;
        foreach (var b in name.Concat(bytes)) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1; }
        PutInt(length, 0, unchecked((int)(crc ^ uint.MaxValue))); output.Write(length);
    }
}

// This models layout allocation and clipping, not Unity's font metrics or its full layout solver.
// Fixed production Rects are recorded exactly; intrinsic GUILayout sizes use documented approximations.
internal static class LayoutRecorder
{
    private sealed class Frame
    {
        public string Kind = "root";
        public Frame? Parent;
        public float X, Y, Width, Height, CursorX, CursorY, UsedWidth, UsedHeight;
        public bool Horizontal;
        public Vector2 Origin;
        public Rect Clip;
        public DrawCommand? Background;
    }
    private static Frame current = Root();
    public static Rect Last { get; private set; }
    public static Vector2 Offset => current.Origin;
    public static Rect Clip => current.Clip;
    private static Frame Root()
    {
        var window = GUI.currentWindow ?? new Rect(0, 0, Screen.width, Screen.height);
        return new Frame { Width = window.width, Height = window.height, Clip = window };
    }
    public static void Reset() { current = Root(); Last = new Rect(0, 0, 200, 32); }
    private static float Option(GUILayoutOption[] options, string kind, float fallback) => options.LastOrDefault(o => o.Kind == kind)?.Value ?? fallback;
    private static Rect Intersect(Rect first, Rect second)
    {
        var x = Math.Max(first.x, second.x); var y = Math.Max(first.y, second.y);
        return new Rect(x, y, Math.Max(0, Math.Min(first.xMax, second.xMax) - x), Math.Max(0, Math.Min(first.yMax, second.yMax) - y));
    }
    private static void Advance(float width, float height)
    {
        current.UsedWidth = Math.Max(current.UsedWidth, current.CursorX + width);
        current.UsedHeight = Math.Max(current.UsedHeight, current.CursorY + height);
        if (current.Horizontal) current.CursorX += width; else current.CursorY += height;
    }
    public static Rect Reserve(float width, float height, GUILayoutOption[] options)
    {
        if (GUILayout.ThrowContent) throw new InvalidOperationException("simulated content/layout failure");
        width = Option(options, "width", Option(options, "expand-width", 1) > 0 ? current.Width - current.CursorX : width);
        height = Option(options, "height", height);
        Last = new Rect(current.X + current.CursorX, current.Y + current.CursorY, Math.Max(0, width), Math.Max(0, height));
        Advance(Last.width, Last.height); return Last;
    }
    public static Rect Control(string text, GUIStyle style, GUILayoutOption[] options)
    {
        var measured = style.CalcSize(new GUIContent(text));
        var width = Option(options, "width", style.fixedWidth > 0 ? style.fixedWidth : current.Horizontal ? Math.Min(current.Width - current.CursorX, measured.x + 16) : current.Width);
        var height = Option(options, "height", style.fixedHeight > 0 ? style.fixedHeight : Math.Max(24, style.CalcHeight(new GUIContent(text), Math.Max(1, width))));
        return Reserve(width, height, new[] { GUILayout.Width(width), GUILayout.Height(height) });
    }
    public static void Space(float amount) { if (current.Horizontal) current.CursorX += amount; else current.CursorY += amount; }
    public static void BeginArea(Rect area)
    {
        var absolute = DrawCapture.Absolute(area);
        current = new Frame { Kind = "area", Parent = current, Width = area.width, Height = area.height, Origin = new(current.Origin.x + area.x, current.Origin.y + area.y), Clip = Intersect(current.Clip, absolute) };
    }
    public static void EndArea() { current = current.Parent ?? Root(); }
    public static void BeginScroll(Vector2 scroll, GUILayoutOption[] options)
    {
        var height = Option(options, "height", Math.Max(0, current.Height - current.CursorY));
        var viewport = new Rect(current.X + current.CursorX, current.Y + current.CursorY, current.Width, height);
        var absolute = DrawCapture.Absolute(viewport);
        var parent = current;
        Advance(viewport.width, viewport.height);
        current = new Frame { Kind = "scroll", Parent = parent, Width = Math.Max(0, viewport.width - 16), Height = height, Origin = new(parent.Origin.x + viewport.x - scroll.x, parent.Origin.y + viewport.y - scroll.y), Clip = Intersect(parent.Clip, absolute) };
    }
    public static void EndScroll() { current = current.Parent ?? Root(); }
    public static void BeginGroup(bool horizontal, GUIStyle? style, GUILayoutOption[] options)
    {
        var x = current.X + current.CursorX; var y = current.Y + current.CursorY;
        var width = Option(options, "width", current.Width - current.CursorX); var height = Option(options, "height", 0);
        DrawCommand? background = null;
        if (style != null && DrawCapture.Enabled)
        {
            DrawCapture.Add("box", new Rect(x, y, width, height), "", style); background = DrawCapture.Commands.Last();
        }
        current = new Frame { Kind = "group", Parent = current, X = x + (style?.padding.left ?? 0), Y = y + (style?.padding.top ?? 0), Width = width - (style?.padding.horizontal ?? 0), Height = height, Horizontal = horizontal, Origin = current.Origin, Clip = current.Clip, Background = background };
    }
    public static void EndGroup()
    {
        var group = current; current = current.Parent ?? Root();
        var width = group.Horizontal ? group.UsedWidth : group.Width;
        var height = Math.Max(group.Height, group.UsedHeight);
        if (group.Background != null) group.Background.Area = group.Background.Area with { height = height };
        Advance(width, height);
    }
}
