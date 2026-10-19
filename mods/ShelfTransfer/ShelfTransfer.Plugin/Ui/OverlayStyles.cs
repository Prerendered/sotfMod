// purpose: the transfer overlay's dark + violet tokens (design.md) and UiScale; separate from Styles so the label editor
//          and typed prompt keep their current look (design Q5: prompt not restyled now)
// owns:    OverlayStyles
// deps:    UnityEngine IMGUI (GUI, GUISkin, GUIStyle), Texture2D, Mathf
// caution: GUI.skin is only valid inside OnGUI, so Get() is called from OnGUI only. Styles are rebuilt only when the
//          scale step changes or Unity unloaded a texture, never per frame

using System.Collections.Generic;

using UnityEngine;

namespace ShelfTransfer.Plugin.Ui;

public sealed class OverlayStyles
{
    // Sizes are base px at a 1080 px screen height (design.md UiScale).
    public const int Space1 = 4;
    public const int Space2 = 8;
    public const int Space4 = 16;
    public const int RowHeight = 34;
    public const int GroupHeaderHeight = 32;
    public const int PanelWidth = 520;
    public const float PanelMaxHeightFraction = 0.7f;
    public const float PanelLeftFraction = 0.05f;

    private const float _referenceHeight = 1080f;
    private const float _minScale = 0.75f;
    private const float _maxScale = 2f;
    private const int _minPanelWidth = 400;
    private const int _titleFontSize = 18;
    private const int _bodyFontSize = 15;
    private const int _smallFontSize = 13;
    private const int _minFontSize = 12;
    private const int _lineLeading = 6;
    private const float _panelEdgeAlpha = 0.4f;
    private const float _pressedDarken = 0.85f;

    public static readonly Color Scrim = Rgb(0x000000, 0.35f);
    public static readonly Color Surface = Rgb(0x14171A, 0.92f);
    public static readonly Color SurfaceRaised = Rgb(0x1F2327);
    public static readonly Color RowHover = Rgb(0x34383D);
    public static readonly Color Foreground = Rgb(0xE8E6E1);
    public static readonly Color Muted = Rgb(0xA9ADB1);
    public static readonly Color Disabled = Rgb(0x959A9F);
    public static readonly Color Accent = Rgb(0xB79CFF);
    public static readonly Color Border = Rgb(0x7D838A);
    public static readonly Color Divider = Rgb(0x3F444A);

    private static OverlayStyles? _cached;

    // Kept so IsAlive can see a texture Unity unloaded (Resources.UnloadUnusedAssets on a scene change).
    private readonly List<Texture2D> _textures = new();

    private OverlayStyles(int scalePercent)
    {
        ScalePercent = scalePercent;
        Scale = scalePercent / 100f;
        GUISkin skin = GUI.skin;

        ScrimTexture = BuildTexture(Scrim);
        PanelEdgeTexture = BuildTexture(new Color(Border.r, Border.g, Border.b, _panelEdgeAlpha));
        PanelTexture = BuildTexture(Surface);
        DividerTexture = BuildTexture(Divider);

        Title = BuildLabel(skin, _titleFontSize, Foreground);
        Title.fontStyle = FontStyle.Bold;
        Body = BuildLabel(skin, _bodyFontSize, Foreground);
        MutedBody = BuildLabel(skin, _bodyFontSize, Muted);
        Small = BuildLabel(skin, _smallFontSize, Muted);
        SmallRight = BuildLabel(skin, _smallFontSize, Muted);
        SmallRight.alignment = TextAnchor.MiddleRight;
        RowDisabled = BuildLabel(skin, _bodyFontSize, Disabled);
        RowDisabled.fontStyle = FontStyle.Italic;
        SmallDisabled = BuildLabel(skin, _smallFontSize, Disabled);
        SmallDisabled.fontStyle = FontStyle.Italic;
        SmallDisabled.alignment = TextAnchor.MiddleRight;
        GroupHeader = BuildLabel(skin, _bodyFontSize, Foreground);
        GroupHeader.fontStyle = FontStyle.Bold;

        Button = BuildButton(skin);
    }

    public int ScalePercent { get; }

    public float Scale { get; }

    public Texture2D ScrimTexture { get; }

    public Texture2D PanelEdgeTexture { get; }

    public Texture2D PanelTexture { get; }

    public Texture2D DividerTexture { get; }

    public GUIStyle Title { get; }

    public GUIStyle Body { get; }

    public GUIStyle MutedBody { get; }

    public GUIStyle Small { get; }

    public GUIStyle SmallRight { get; }

    public GUIStyle RowDisabled { get; }

    public GUIStyle SmallDisabled { get; }

    public GUIStyle GroupHeader { get; }

    /// <summary>Secondary button: SurfaceRaised fill with a 1 px Border edge, RowHover on hover.</summary>
    public GUIStyle Button { get; }

    public float PanelWidthPx => Mathf.Max(Px(PanelWidth), _minPanelWidth);

    public float TitleLineHeight => Title.fontSize + _lineLeading;

    public float BodyLineHeight => Body.fontSize + _lineLeading;

    public float SmallLineHeight => Small.fontSize + _lineLeading;

    private bool IsAlive => _textures.TrueForAll(texture => texture != null);

    /// <summary>Styles for the current screen height; call from OnGUI only.</summary>
    public static OverlayStyles Get()
    {
        float scale = Mathf.Clamp(Screen.height / _referenceHeight, _minScale, _maxScale);
        int scalePercent = Mathf.RoundToInt(scale * 100f);
        if (_cached is null || _cached.ScalePercent != scalePercent || !_cached.IsAlive)
        {
            _cached = new OverlayStyles(scalePercent);
        }

        return _cached;
    }

    public float Px(int basePx) => Mathf.RoundToInt(basePx * Scale);

    private GUIStyle BuildLabel(GUISkin skin, int baseFontSize, Color color)
    {
        GUIStyle style = new(skin.label)
        {
            fontSize = ScaleFont(baseFontSize),
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(0, 0, 0, 0),
            // Labels carry User label/group text; a "<size=500>" in a hand-edited label file must render as typed.
            richText = false,
        };
        style.normal.textColor = color;
        return style;
    }

    private GUIStyle BuildButton(GUISkin skin)
    {
        GUIStyle style = new(skin.button) { fontSize = ScaleFont(_bodyFontSize), alignment = TextAnchor.MiddleCenter };
        // One px frame in a 3x3 texture; border 1 keeps the edge 1 px at any size (9-slice).
        style.border = new RectOffset(1, 1, 1, 1);
        SetState(style.normal, BuildFramedTexture(SurfaceRaised, Border), Foreground);
        SetState(style.hover, BuildFramedTexture(RowHover, Border), Foreground);
        SetState(style.active, BuildFramedTexture(Darken(SurfaceRaised), Border), Foreground);
        SetState(style.focused, style.normal.background, Foreground);
        return style;
    }

    private int ScaleFont(int baseFontSize) => Mathf.Max(Mathf.RoundToInt(baseFontSize * Scale), _minFontSize);

    private static void SetState(GUIStyleState state, Texture2D background, Color text)
    {
        state.background = background;
        state.textColor = text;
    }

    private static Color Darken(Color color) =>
        new(color.r * _pressedDarken, color.g * _pressedDarken, color.b * _pressedDarken, color.a);

    // HideAndDontSave keeps a scene unload from destroying it; IsAlive covers an explicit unload anyway.
    private Texture2D BuildTexture(Color color)
    {
        Texture2D texture = new(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        _textures.Add(texture);
        return texture;
    }

    private Texture2D BuildFramedTexture(Color fill, Color edge)
    {
        Texture2D texture = new(3, 3) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
        for (int x = 0; x < 3; x++)
        {
            for (int y = 0; y < 3; y++)
            {
                texture.SetPixel(x, y, x == 1 && y == 1 ? fill : edge);
            }
        }

        texture.Apply();
        _textures.Add(texture);
        return texture;
    }

    private static Color Rgb(int hex, float alpha = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, alpha);
}
