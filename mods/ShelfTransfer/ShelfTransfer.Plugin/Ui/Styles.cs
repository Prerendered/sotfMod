// purpose: the shared IMGUI look of every mod panel: light grey panel, dark text, violet accent (plan.md Design direction)
// owns:    Styles
// deps:    UnityEngine IMGUI (GUI, GUISkin, GUIStyle), Texture2D
// caution: GUI.skin is only valid inside OnGUI, so Get() is called from OnGUI only. Text fields and buttons keep the
//          default skin's dark look so their focused and hover states stay readable without restyling each one

using UnityEngine;

namespace ShelfTransfer.Plugin.Ui;

public sealed class Styles
{
    private const int _titleFontSize = 17;
    private const int _bodyFontSize = 14;
    private const int _smallFontSize = 12;
    private const int _fieldFontSize = 15;
    private const int _panelPadding = 12;

    private static readonly Color _panelColor = new(0.90f, 0.90f, 0.92f, 0.97f);
    private static readonly Color _textColor = new(0.11f, 0.11f, 0.13f, 1f);
    private static readonly Color _mutedColor = new(0.38f, 0.38f, 0.42f, 1f);
    private static readonly Color _errorColor = new(0.70f, 0.10f, 0.10f, 1f);

    private static Styles? _cached;

    // Kept so IsAlive can see a texture Unity unloaded (Resources.UnloadUnusedAssets on a scene change).
    private readonly Texture2D _panelTexture;

    private Styles()
    {
        GUISkin skin = GUI.skin;
        _panelTexture = BuildTexture(_panelColor);

        Panel = new GUIStyle(skin.box) { padding = new RectOffset(_panelPadding, _panelPadding, _panelPadding, _panelPadding) };
        Panel.normal.background = _panelTexture;

        Title = BuildLabel(skin, _titleFontSize, _textColor);
        Title.fontStyle = FontStyle.Bold;
        Body = BuildLabel(skin, _bodyFontSize, _textColor);
        Muted = BuildLabel(skin, _smallFontSize, _mutedColor);
        Accent = BuildLabel(skin, _smallFontSize, AccentColor);
        Error = BuildLabel(skin, _bodyFontSize, _errorColor);
        CounterRight = BuildLabel(skin, _smallFontSize, _mutedColor);
        CounterRight.alignment = TextAnchor.MiddleRight;

        TextField = new GUIStyle(skin.textField) { fontSize = _fieldFontSize, alignment = TextAnchor.MiddleLeft };
        Button = new GUIStyle(skin.button) { fontSize = _bodyFontSize };
    }

    /// <summary>Tint for GUI.backgroundColor on a selected button, and the colour of accent notes.</summary>
    public static Color AccentColor { get; } = new(0.47f, 0.32f, 0.82f, 1f);

    public GUIStyle Panel { get; }

    public GUIStyle Title { get; }

    public GUIStyle Body { get; }

    public GUIStyle Muted { get; }

    public GUIStyle Accent { get; }

    public GUIStyle Error { get; }

    public GUIStyle CounterRight { get; }

    public GUIStyle TextField { get; }

    public GUIStyle Button { get; }

    private bool IsAlive => _panelTexture != null;

    /// <summary>Built on first use and rebuilt if Unity destroyed the panel texture; call from OnGUI only.</summary>
    public static Styles Get()
    {
        if (_cached is null || !_cached.IsAlive)
        {
            _cached = new Styles();
        }

        return _cached;
    }

    private static GUIStyle BuildLabel(GUISkin skin, int fontSize, Color color)
    {
        GUIStyle style = new(skin.label) { fontSize = fontSize, wordWrap = true, alignment = TextAnchor.MiddleLeft };
        style.normal.textColor = color;
        return style;
    }

    // HideAndDontSave keeps a scene unload from destroying it; IsAlive covers an explicit unload anyway.
    private static Texture2D BuildTexture(Color color)
    {
        Texture2D texture = new(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }
}
