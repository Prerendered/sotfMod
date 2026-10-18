// purpose: the label editor (F3): label key while aiming at a shelf opens an IMGUI panel to name it and pick or create a group
// owns:    LabelEditorUi
// deps:    LabelsFeature (Store, SetLabel, IsValidName), ModConfig, Styles, ShelfRegistry.BuildShelfRef, IModLog,
//          interop Sons.Input (InputActionMapState, InputCursorState), Sons.Gameplay.ItemStorageController
// caution: I5 - writes only through LabelsFeature.SetLabel. I2 - reads the aimed shelf and switches only the game's local
//          input state, never shelf or network state. Input is blocked by the game's own InputActionMapState (Console)
//          and InputCursorState on a private object: active = blocked, inactive = the game restores its own state.
//          Release waits for Enter/Esc to be up so the closing key never reaches the game. Update and OnGUI are P2
//          boundaries: a throw logs error event=ui.label, gives input back at once and halts the editor for the session

using System;
using System.Collections.Generic;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;

using Sons.Gameplay;
using Sons.Input;
using TheForest.Utils;

using UnityEngine;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Plugin.Config;
using ShelfTransfer.Plugin.Game;
using ShelfTransfer.Plugin.Labels;

using LogLevel = ShelfTransfer.Core.Logging.LogLevel;
using UnityObject = UnityEngine.Object;

namespace ShelfTransfer.Plugin.Ui;

public sealed class LabelEditorUi : MonoBehaviour
{
    // Same reach as AimWatcher and the discovery dump: every usable shelf, without distant scenery.
    private const float _maxAimDistance = 10f;

    // RedLoader's SUI panels use 100 for the same component; above the game's world cursor state.
    private const int _cursorPriority = 100;

    // Two frames lets the game's cursor state settle (it may apply in LateUpdate) before the restore check reads it.
    private const int _restoreCheckDelayFrames = 2;

    private const float _toastSeconds = 4f;
    private const string _nameControl = "ShelfTransfer.LabelName";
    private const string _inputBlockName = "ShelfTransfer.LabelEditorInput";

    private const float _panelWidth = 460f;
    private const float _panelTopFraction = 0.22f;
    private const float _padding = 16f;
    private const float _lineHeight = 22f;
    private const float _fieldHeight = 28f;
    private const float _gap = 6f;
    private const float _counterWidth = 60f;
    private const float _newGroupLabelWidth = 90f;
    private const float _footerButtonWidth = 90f;
    private const float _toastWidth = 360f;
    private const float _toastTop = 40f;
    private const int _groupColumns = 3;
    private const int _maxVisibleGroupRows = 4;

    private const string _titleText = "Label shelf";
    private const string _nameText = "Name";
    private const string _groupText = "Group";
    private const string _newGroupText = "New group";
    private const string _noGroupText = "None";
    private const string _saveText = "Save";
    private const string _cancelText = "Cancel";
    private const string _hintText = "Enter saves, Esc cancels. Empty name and no group clears the label.";
    private const string _invalidCharText = "Names cannot contain control characters.";
    private const string _savedText = "Label saved";
    private const string _saveFailedText = "Could not save labels, see log";
    private const string _saveBlockedText = "Labels are read-only this session, see log";
    private const string _noStoreText = "Labels are not available, see log";

    private static readonly string _tooLongText = $"Names stop at {LabelsFeature.MaxNameLength} characters; longer text is cut.";

    private IModLog? _log;
    private ModConfig? _config;
    private bool _isHalted;

    private GameObject? _inputBlock;
    private bool _isReleasing;
    private int _restoreCheckFrame = -1;
    private string _closeReason = string.Empty;
    private CursorLockMode _cursorBefore;
    private bool _wasCursorVisible;

    private bool _isOpen;
    private LabelStore? _store;
    private ItemStorageController? _shelf;
    private string _key = string.Empty;
    private string _typeName = string.Empty;
    private string _name = string.Empty;
    private string? _selectedGroup;
    private string _newGroup = string.Empty;
    private IReadOnlyList<string> _groups = Array.Empty<string>();
    private Vector2 _groupScroll;
    private string? _note;
    private int _focusFrame = -1;

    private string? _toast;
    private bool _isToastError;
    private float _toastUntil;

    public LabelEditorUi(IntPtr ptr) : base(ptr) { }

    // Managed parameters cannot cross into Il2Cpp; hidden so ClassInjector does not try to expose it.
    [HideFromIl2Cpp]
    public void Init(IModLog log, ModConfig config)
    {
        _log = log;
        _config = config;
    }

    public void Update()
    {
        if (_log is null || _config is null || _isHalted)
        {
            return;
        }

        try
        {
            Tick(_log, _config);
        }
        catch (Exception ex)
        {
            Halt(_log, ex);
        }
    }

    public void OnGUI()
    {
        if (_log is null || _isHalted || (!_isOpen && _toast is null))
        {
            return;
        }

        try
        {
            Draw(_log);
        }
        catch (Exception ex)
        {
            Halt(_log, ex);
        }
    }

    public void OnDestroy() => ReleaseInputNow();

    [HideFromIl2Cpp]
    private void Tick(IModLog log, ModConfig config)
    {
        if (_isReleasing)
        {
            TryFinishRelease();
        }
        else if (_restoreCheckFrame >= 0 && Time.frameCount >= _restoreCheckFrame)
        {
            WriteRestoreCheck(log);
        }

        if (!_isOpen)
        {
            // Not checked while open: the label key is then just a letter typed into the name.
            if (Input.GetKeyDown(config.LabelKey.Value))
            {
                TryOpen(log);
            }

            return;
        }

        string? lostReason = GetLostReason();
        if (lostReason is not null)
        {
            Cancel(log, lostReason);
        }
        else if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cancel(log, "esc");
        }
        else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            Submit(log);
        }
    }

    [HideFromIl2Cpp]
    private void TryOpen(IModLog log)
    {
        LabelStore? store = LabelsFeature.Store;
        if (store is null)
        {
            WriteNoOpen(log, "no_store");
            return;
        }

        // A free cursor means a game menu, inventory or chat owns the screen; opening over it would fight its input.
        CursorLockMode cursor = Cursor.lockState;
        if (cursor != CursorLockMode.Locked)
        {
            WriteNoOpen(log, "cursor_free", cursor.ToString());
            return;
        }

        ItemStorageController? shelf = FindAimedShelf();
        if (shelf == null)
        {
            WriteNoOpen(log, "no_shelf");
            return;
        }

        ShelfRef shelfRef = ShelfRegistry.BuildShelfRef(shelf);
        ShelfLabel? existing = store.Get(shelfRef.Key);
        _store = store;
        _shelf = shelf;
        _key = shelfRef.Key;
        _typeName = shelfRef.TypeName;
        _name = existing?.Label ?? string.Empty;
        _selectedGroup = existing?.Group;
        _newGroup = string.Empty;
        _groups = store.ListGroups();
        _groupScroll = Vector2.zero;
        _note = null;
        _toast = null;
        // Next frame: focusing now would let this frame's label-key event type its letter into the field.
        _focusFrame = Time.frameCount + 1;

        BlockInput();
        _isOpen = true;
        log.Write(LogLevel.Info, "ui.label", new KeyValuePair<string, object?>[]
        {
            new("action", "open"),
            new("shelf", _key),
            new("label", existing?.Label),
            new("group", existing?.Group),
            new("groups", _groups.Count),
        });
    }

    [HideFromIl2Cpp]
    private string? GetLostReason()
    {
        if (!ReferenceEquals(LabelsFeature.Store, _store))
        {
            return "world_left";
        }

        return _shelf == null ? "shelf_gone" : null;
    }

    [HideFromIl2Cpp]
    private void Submit(IModLog log)
    {
        string name = _name.Trim();
        string? group = GetChosenGroup();
        if (!LabelsFeature.IsValidName(name) || !LabelsFeature.IsValidName(group))
        {
            bool isTooLong = name.Length > LabelsFeature.MaxNameLength || (group?.Length ?? 0) > LabelsFeature.MaxNameLength;
            _note = isTooLong ? _tooLongText : _invalidCharText;
            log.Write(LogLevel.Info, "ui.label", new KeyValuePair<string, object?>[]
            {
                new("action", "reject"),
                new("shelf", _key),
                new("reason", isTooLong ? "too_long" : "invalid_char"),
            });
            return;
        }

        string action = name.Length == 0 && group is null ? "clear" : "set";
        string key = _key;
        log.Write(LogLevel.Info, "ui.label", new KeyValuePair<string, object?>[]
        {
            new("action", action),
            new("shelf", key),
            new("label", name),
            new("group", group),
        });

        // Closed first, so a throw from the save still leaves the editor shut and input on its way back.
        CloseEditor(action);
        ShowSaveResult(LabelsFeature.SetLabel(key, name, group));
    }

    [HideFromIl2Cpp]
    private void Cancel(IModLog log, string reason)
    {
        log.Write(LogLevel.Info, "ui.label", new KeyValuePair<string, object?>[]
        {
            new("action", "cancel"),
            new("shelf", _key),
            new("reason", reason),
        });
        CloseEditor(reason);
    }

    // A typed new group wins over the picked one, so typing "Main base" both creates and assigns it.
    [HideFromIl2Cpp]
    private string? GetChosenGroup()
    {
        string typed = _newGroup.Trim();
        if (typed.Length > 0)
        {
            return typed;
        }

        return string.IsNullOrWhiteSpace(_selectedGroup) ? null : _selectedGroup;
    }

    [HideFromIl2Cpp]
    private void ShowSaveResult(LabelSaveResult? saved)
    {
        (string text, bool isError) = saved?.Status switch
        {
            LabelSaveStatus.Saved => (_savedText, false),
            LabelSaveStatus.Failed => (_saveFailedText, true),
            LabelSaveStatus.Blocked => (_saveBlockedText, true),
            _ => (_noStoreText, true),
        };
        _toast = text;
        _isToastError = isError;
        _toastUntil = Time.unscaledTime + _toastSeconds;
    }

    [HideFromIl2Cpp]
    private void CloseEditor(string reason)
    {
        ResetEditor();
        _closeReason = reason;
        _isReleasing = true;
    }

    [HideFromIl2Cpp]
    private void ResetEditor()
    {
        _isOpen = false;
        _store = null;
        _shelf = null;
        _name = string.Empty;
        _newGroup = string.Empty;
        _selectedGroup = null;
        _groups = Array.Empty<string>();
        _note = null;
        _focusFrame = -1;
    }

    [HideFromIl2Cpp]
    private void Halt(IModLog log, Exception ex)
    {
        _isHalted = true;
        log.WriteException("ui.label", ex);
        ResetEditor();
        _isReleasing = false;
        ReleaseInputNow();
    }

    [HideFromIl2Cpp]
    private void BlockInput()
    {
        _cursorBefore = Cursor.lockState;
        _wasCursorVisible = Cursor.visible;
        _isReleasing = false;
        _restoreCheckFrame = -1;
        if (_inputBlock == null)
        {
            _inputBlock = CreateInputBlock();
        }

        _inputBlock.SetActive(true);
    }

    [HideFromIl2Cpp]
    private void TryFinishRelease()
    {
        if (Input.GetKey(KeyCode.Escape) || Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter))
        {
            return;
        }

        _isReleasing = false;
        ReleaseInputNow();
        _restoreCheckFrame = Time.frameCount + _restoreCheckDelayFrames;
    }

    [HideFromIl2Cpp]
    private void ReleaseInputNow()
    {
        if (_inputBlock != null)
        {
            _inputBlock.SetActive(false);
        }
    }

    // Evidence for "no stuck cursor": after an Enter/Esc close the cursor must be back where it was at open.
    [HideFromIl2Cpp]
    private void WriteRestoreCheck(IModLog log)
    {
        _restoreCheckFrame = -1;
        CursorLockMode cursor = Cursor.lockState;
        bool isVisible = Cursor.visible;
        bool isRestored = cursor == _cursorBefore && isVisible == _wasCursorVisible;
        // Leaving the world legitimately frees the cursor for the menu, so only an in-world close must match.
        bool shouldMatch = _closeReason is not ("world_left" or "shelf_gone");
        log.Write(isRestored || !shouldMatch ? LogLevel.Info : LogLevel.Warn, "ui.label", new KeyValuePair<string, object?>[]
        {
            new("action", "input"),
            new("outcome", isRestored ? "restored" : "changed"),
            new("reason", _closeReason),
            new("cursor", cursor.ToString()),
            new("visible", isVisible),
            new("cursor_before", _cursorBefore.ToString()),
            new("visible_before", _wasCursorVisible),
        });
    }

    [HideFromIl2Cpp]
    private void Draw(IModLog log)
    {
        Styles styles = Styles.Get();
        if (_isOpen)
        {
            DrawEditor(log, styles);
            return;
        }

        if (Time.unscaledTime >= _toastUntil)
        {
            _toast = null;
            return;
        }

        Rect toast = new((Screen.width - _toastWidth) / 2f, _toastTop, _toastWidth, _fieldHeight + (2f * _gap));
        GUI.Box(toast, string.Empty, styles.Panel);
        GUI.Label(Inset(toast, _gap), _toast, _isToastError ? styles.Error : styles.Body);
    }

    [HideFromIl2Cpp]
    private void DrawEditor(IModLog log, Styles styles)
    {
        int groupCount = _groups.Count + 1;
        int groupRows = (groupCount + _groupColumns - 1) / _groupColumns;
        float groupAreaHeight = Math.Min(groupRows, _maxVisibleGroupRows) * (_fieldHeight + _gap);
        // 5 text lines + a 2-line note, 3 field rows (name, new group, buttons), then the group grid.
        float panelHeight = (2f * _padding) + (7f * (_lineHeight + _gap)) + (3f * (_fieldHeight + _gap)) + groupAreaHeight;
        Rect panel = new((Screen.width - _panelWidth) / 2f, Screen.height * _panelTopFraction, _panelWidth, panelHeight);
        GUI.Box(panel, string.Empty, styles.Panel);

        float x = panel.x + _padding;
        float width = panel.width - (2f * _padding);
        float y = panel.y + _padding;

        GUI.Label(new Rect(x, y, width, _lineHeight), _titleText, styles.Title);
        y += _lineHeight + _gap;
        GUI.Label(new Rect(x, y, width, _lineHeight), _typeName, styles.Muted);
        y += _lineHeight + _gap;

        GUI.Label(new Rect(x, y, width, _lineHeight), _nameText, styles.Body);
        GUI.Label(new Rect(x + width - _counterWidth, y, _counterWidth, _lineHeight),
            $"{_name.Length}/{LabelsFeature.MaxNameLength}", styles.CounterRight);
        y += _lineHeight + _gap;
        GUI.SetNextControlName(_nameControl);
        _name = GUI.TextField(new Rect(x, y, width, _fieldHeight), _name, LabelsFeature.MaxNameLength, styles.TextField);
        if (_focusFrame >= 0 && Time.frameCount >= _focusFrame)
        {
            GUI.FocusControl(_nameControl);
            _focusFrame = -1;
        }

        y += _fieldHeight + _gap;

        GUI.Label(new Rect(x, y, width, _lineHeight), _groupText, styles.Body);
        y += _lineHeight + _gap;
        DrawGroupButtons(styles, new Rect(x, y, width, groupAreaHeight), groupRows);
        y += groupAreaHeight;

        GUI.Label(new Rect(x, y, _newGroupLabelWidth, _fieldHeight), _newGroupText, styles.Body);
        _newGroup = GUI.TextField(new Rect(x + _newGroupLabelWidth, y, width - _newGroupLabelWidth, _fieldHeight),
            _newGroup, LabelsFeature.MaxNameLength, styles.TextField);
        y += _fieldHeight + _gap;

        DrawNote(styles, new Rect(x, y, width, (2f * _lineHeight) + _gap));
        y += (2f * _lineHeight) + (2f * _gap);
        GUI.Label(new Rect(x, y, width, _lineHeight), _hintText, styles.Muted);
        y += _lineHeight + _gap;

        float buttonsX = x + width - (2f * _footerButtonWidth) - _gap;
        if (GUI.Button(new Rect(buttonsX, y, _footerButtonWidth, _fieldHeight), _saveText, styles.Button))
        {
            Submit(log);
        }
        else if (GUI.Button(new Rect(buttonsX + _footerButtonWidth + _gap, y, _footerButtonWidth, _fieldHeight), _cancelText, styles.Button))
        {
            Cancel(log, "button");
        }
    }

    // "None" first, then each existing group; the picked one is tinted unless a typed new group overrides it.
    [HideFromIl2Cpp]
    private void DrawGroupButtons(Styles styles, Rect area, int rows)
    {
        float buttonWidth = (area.width - ((_groupColumns - 1) * _gap)) / _groupColumns;
        Rect content = new(0f, 0f, area.width - (2f * _gap), rows * (_fieldHeight + _gap));
        _groupScroll = GUI.BeginScrollView(area, _groupScroll, content);
        bool isTypedGroup = _newGroup.Trim().Length > 0;
        Color background = GUI.backgroundColor;
        for (int index = 0; index <= _groups.Count; index++)
        {
            string? group = index == 0 ? null : _groups[index - 1];
            bool isSelected = !isTypedGroup && string.Equals(group, _selectedGroup, StringComparison.Ordinal);
            Rect button = new((index % _groupColumns) * (buttonWidth + _gap), (index / _groupColumns) * (_fieldHeight + _gap),
                buttonWidth - _gap, _fieldHeight);
            GUI.backgroundColor = isSelected ? Styles.AccentColor : background;
            if (GUI.Button(button, group ?? _noGroupText, styles.Button))
            {
                _selectedGroup = group;
                _newGroup = string.Empty;
            }
        }

        GUI.backgroundColor = background;
        GUI.EndScrollView();
    }

    [HideFromIl2Cpp]
    private void DrawNote(Styles styles, Rect area)
    {
        if (_note is not null)
        {
            GUI.Label(area, _note, styles.Error);
        }
        else if (_name.Length >= LabelsFeature.MaxNameLength || _newGroup.Length >= LabelsFeature.MaxNameLength)
        {
            GUI.Label(area, _tooLongText, styles.Accent);
        }
    }

    [HideFromIl2Cpp]
    private static void WriteNoOpen(IModLog log, string reason, string? cursor = null)
    {
        List<KeyValuePair<string, object?>> line = new()
        {
            new("action", "none"),
            new("reason", reason),
        };
        if (cursor is not null)
        {
            line.Add(new("cursor", cursor));
        }

        log.Write(LogLevel.Info, "ui.label", line);
    }

    private static Rect Inset(Rect rect, float by) =>
        new(rect.x + by, rect.y + by, rect.width - (2f * by), rect.height - (2f * by));

    // Built inactive so both components hold their settings before OnEnable; SetActive(true) is then the game's own
    // "UI open" signal (map state + hardware cursor), and SetActive(false) lets the game restore what it had before.
    private static GameObject CreateInputBlock()
    {
        GameObject block = new(_inputBlockName);
        block.SetActive(false);
        UnityObject.DontDestroyOnLoad(block);

        InputActionMapState map = AddGameComponent<InputActionMapState>(block);
        map._applyState = InputState.Console;
        InputCursorState cursor = AddGameComponent<InputCursorState>(block);
        cursor._enabled = true;
        cursor._hardwareCursor = true;
        cursor._priority = _cursorPriority;
        return block;
    }

    // Non-generic AddComponent plus TryCast: the generic overload depends on an inflated method existing in the game.
    private static T AddGameComponent<T>(GameObject target) where T : Component
    {
        Component added = target.AddComponent(Il2CppType.Of<T>());
        return added.TryCast<T>() ?? throw new InvalidOperationException($"AddComponent did not return {typeof(T).Name}");
    }

    // Any storage shelf, stocked or empty: AimWatcher.GetCurrent is null on an empty slot or the frame, so the
    // editor casts its own ray on the key press only (same nearest-non-player-hit rule as AimWatcher).
    private static ItemStorageController? FindAimedShelf()
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            return null;
        }

        Transform view = camera.transform;
        Transform playerRoot = view.root;
        List<RaycastHit> hits = new(Physics.RaycastAll(view.position, view.forward, _maxAimDistance,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide));
        Collider? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (RaycastHit hit in hits)
        {
            float distance = hit.distance;
            if (distance >= nearestDistance)
            {
                continue;
            }

            Collider hitCollider = hit.collider;
            if (hitCollider == null || hitCollider.transform.IsChildOf(playerRoot))
            {
                continue;
            }

            nearest = hitCollider;
            nearestDistance = distance;
        }

        if (nearest == null)
        {
            return null;
        }

        Component? found = nearest.GetComponentInParent(Il2CppType.Of<ItemStorageController>());
        return found == null ? null : found.TryCast<ItemStorageController>();
    }
}
