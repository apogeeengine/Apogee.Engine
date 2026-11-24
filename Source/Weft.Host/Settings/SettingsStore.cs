using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Weft.Content.FileSystem;
using Weft.Content.Xml;
using Weft.Core.Logging;
using Weft.Input;
using Weft.Reflection;
using Weft.Scripting;

namespace Weft.Host;

/// <summary>
/// The player's settings, as the <c>settings</c> global: typed values under dotted keys (<c>audio.volume</c>, <c>mod.coolmod.show_hints</c>) that the engine and the game define (<see cref="SettingDef"/>), kept in
/// <c>user://settings.xml</c> as the ones that differ from their defaults. Keys the game does not define (a mod that is switched off) are kept as they are. The store belongs to the main thread.
/// </summary>
[WeftClass(Name = "Settings", Creatable = true)]
public sealed partial class SettingsStore : EngineObject
{
    /// <summary>Where the settings of a game are.</summary>
    public const string DefaultPath = "user://settings.xml";

    private const int FormatVersion = 1;
    private const string EmptyDocument = "<settings version=\"1\"/>\n";

    private readonly VirtualFileSystem? _files;
    private readonly string? _path;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private InputActionMap? _map;
    private Dictionary<string, string[]> _savedBindings = new(StringComparer.Ordinal); // what the file says, until a map takes it over
    private bool _dirty;
    private bool _attempted;
    private bool _failing;
    private long _lastAttempt;
    private string? _lastWritten;

    private sealed class Slot(string key)
    {
        public string Key { get; } = key;

        public SettingDef? Def { get; set; }

        public Value Value { get; set; }

        /// <summary>The value this run started with, which a setting that needs a restart is compared to.</summary>
        public Value Applied { get; set; }

        /// <summary>The text from the file, until a definition reads it (and for good when none does).</summary>
        public string? Raw { get; set; }
    }

    /// <summary>A store in memory that is never written: for a script run on its own, and for tests.</summary>
    public SettingsStore()
    {
    }

    private SettingsStore(VirtualFileSystem files, string path)
    {
        _files = files;
        _path = path;
    }

    /// <summary>
    /// The settings in <paramref name="path"/>, or the defaults when there is no such file. A file that cannot be read is logged and set aside as <c>settings.bad.xml</c> beside it; the defaults are used and the
    /// next save replaces it.
    /// </summary>
    /// <param name="files">The game's files; <paramref name="path"/> must be in a writable source (<c>user://</c>).</param>
    /// <param name="path">The file.</param>
    public static SettingsStore Open(VirtualFileSystem files, string path = DefaultPath)
    {
        var store = new SettingsStore(files, path);
        store.Read();
        return store;
    }

    /// <summary>The clock that the debounce of <see cref="SaveIfDirty"/> counts on; tests set a fake one.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Raised when a key is defined (also when it is defined again), with the key: what hooks a setting up to the thing it controls.</summary>
    public event Action<string>? KeyDefined;

    /// <summary>A setting changed, with its key. It is raised at once, after the store holds the new value, and only when the value is really different.</summary>
    [Signal]
    public Signal<string> Changed { get; } = new();

    /// <summary>True when a setting that takes effect at the next start (<see cref="SettingDef.Restart"/>) holds another value than this run started with.</summary>
    [Property]
    public bool NeedsRestart => _slots.Values.Any(s => s.Def is { Restart: true } && s.Value != s.Applied);

    /// <summary>True when there are changes that are not in the file yet.</summary>
    public bool IsDirty => _dirty;

    // ---- Defining ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Defines a setting. A value the file has for the key is read now; one that does not read as the type is logged and the default is used. Defining a key again with the same type and owner (a
    /// script that reloaded) replaces its definition and keeps its value.
    /// </summary>
    /// <exception cref="ValueException">The definition is wrong, or the key is defined as another type or by another owner.</exception>
    public void Define(SettingDef definition)
    {
        SettingDef def = definition.Validated();
        if (def.Key.StartsWith("audio.bus.", StringComparison.Ordinal) && def.Type != SettingType.Number)
        {
            throw new ValueException($"setting '{def.Key}': the volume of an audio bus is a number");
        }

        if (!_slots.TryGetValue(def.Key, out Slot? slot))
        {
            _slots[def.Key] = slot = new Slot(def.Key);
        }

        if (slot.Def is { } old)
        {
            if (old.Owner != def.Owner)
            {
                throw new ValueException(old.Owner is null
                    ? $"'{def.Key}' is a setting of the engine and cannot be defined again"
                    : $"'{def.Key}' is already defined by {old.Owner}");
            }

            if (old.Type != def.Type)
            {
                throw new ValueException($"'{def.Key}' is defined as {SettingDef.TypeName(old.Type)}, not {SettingDef.TypeName(def.Type)}");
            }

            slot.Def = def;
            Value kept = TryCoerce(def, slot.Value, out Value fitted) ? fitted : def.Default;
            if (kept != slot.Value)
            {
                slot.Value = kept;
                MarkChanged(slot);
            }
        }
        else
        {
            slot.Def = def;
            slot.Value = def.Default;
            if (slot.Raw is { } raw)
            {
                if (def.TryParse(raw, out Value read))
                {
                    slot.Value = read;
                }
                else
                {
                    Log.Warn("Settings", $"'{def.Key}' is '{raw}' in the file, which is not {SettingDef.TypeName(def.Type)}{(def.Choices is null ? "" : " (one of " + string.Join(", ", def.Choices) + ")")}: the default {SettingDef.Format(def.Default)} is used.");
                }

                slot.Raw = null;
            }

            slot.Applied = slot.Value;
        }

        KeyDefined?.Invoke(def.Key);
    }

    private static bool TryCoerce(SettingDef def, Value value, out Value fitted)
    {
        try
        {
            fitted = def.Coerce(value);
            return true;
        }
        catch (ValueException)
        {
            fitted = default;
            return false;
        }
    }

    /// <summary>
    /// Defines a setting from a script. <paramref name="spec"/> is <c>{ type = "bool" | "int" | "number" | "string" | "choice", default = …, min = …, max = …, choices = { … }, restart = …, help = "…" }</c>. The
    /// script that defines it owns it; a mod can define only <c>mod.&lt;its id&gt;.…</c> keys and audio buses (<c>audio.bus.&lt;name&gt;</c>, a number).
    /// </summary>
    /// <param name="key">Two or more words of <c>a-z</c>, <c>0-9</c> and <c>_</c> joined by dots.</param>
    /// <param name="spec">The type and default, and what else it needs.</param>
    [Method]
    public void Define(string key, ScriptTable spec)
    {
        ScriptOwner? caller = Caller;
        if (caller is { IsMod: true } && !key.StartsWith($"mod.{caller.Id}.", StringComparison.Ordinal) && !key.StartsWith("audio.bus.", StringComparison.Ordinal))
        {
            throw NotYours(caller);
        }

        Define(ReadSpec(key, spec) with { Owner = caller?.Id });
    }

    private static readonly string[] SpecFields = ["type", "default", "min", "max", "choices", "restart", "help"];

    private static SettingDef ReadSpec(string key, ScriptTable spec)
    {
        foreach (string field in spec.KeysAt([]))
        {
            if (Array.IndexOf(SpecFields, field) < 0)
            {
                throw new ValueException($"setting '{key}': '{field}' is not part of a setting (use {string.Join(", ", SpecFields)})");
            }
        }

        Value Field(string name) => spec.KindAt([DataPathSegment.Field(name)]) == ScriptDataKind.Scalar ? spec.GetAt([DataPathSegment.Field(name)]) : default;

        Value typeName = Field("type");
        if (typeName.Kind != ValueKind.String || !SettingDef.TryParseType(typeName.ToStringValue(), out SettingType type))
        {
            throw new ValueException($"setting '{key}': type must be \"bool\", \"int\", \"number\", \"string\" or \"choice\"");
        }

        Value @default = Field("default");
        if (@default.IsNil)
        {
            throw new ValueException($"setting '{key}': a setting needs a default");
        }

        double min = double.NegativeInfinity;
        double max = double.PositiveInfinity;
        if (Field("min") is { IsNil: false } low)
        {
            min = low.Kind is ValueKind.Int or ValueKind.Float ? low.ToDouble() : throw new ValueException($"setting '{key}': min must be a number");
        }

        if (Field("max") is { IsNil: false } high)
        {
            max = high.Kind is ValueKind.Int or ValueKind.Float ? high.ToDouble() : throw new ValueException($"setting '{key}': max must be a number");
        }

        List<string>? choices = null;
        if (spec.KindAt([DataPathSegment.Field("choices")]) is not ScriptDataKind.Missing)
        {
            if (spec.KindAt([DataPathSegment.Field("choices")]) != ScriptDataKind.Array)
            {
                throw new ValueException($"setting '{key}': choices must be a list of strings");
            }

            choices = [];
            int count = spec.LengthAt([DataPathSegment.Field("choices")]);
            for (int i = 0; i < count; i++)
            {
                Value item = spec.GetAt([DataPathSegment.Field("choices"), DataPathSegment.Element(i)]);
                choices.Add(item.Kind == ValueKind.String ? item.ToStringValue() : throw new ValueException($"setting '{key}': choices must be a list of strings"));
            }
        }

        bool restart = false;
        if (Field("restart") is { IsNil: false } restartValue)
        {
            restart = restartValue.Kind == ValueKind.Bool ? restartValue.ToBool() : throw new ValueException($"setting '{key}': restart must be true or false");
        }

        string? help = null;
        if (Field("help") is { IsNil: false } helpValue)
        {
            help = helpValue.Kind == ValueKind.String ? helpValue.ToStringValue() : throw new ValueException($"setting '{key}': help must be a string");
        }

        return new SettingDef(key, type, @default, min, max, choices, restart, help);
    }

    // ---- Reading and writing values ---------------------------------------------------------------------------------------------

    /// <summary>The value of a setting.</summary>
    /// <param name="key">A defined key.</param>
    /// <exception cref="ValueException">There is no such setting.</exception>
    [Method]
    public Value Get(string key) => Defined(key).Value;

    /// <summary>The value of a setting, or false when the key is not defined.</summary>
    public bool TryGet(string key, out Value value)
    {
        if (_slots.TryGetValue(key, out Slot? slot) && slot.Def is not null)
        {
            value = slot.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>A boolean setting's value.</summary>
    public bool GetBool(string key) => Defined(key).Value.ToBool();

    /// <summary>An integer setting's value.</summary>
    public int GetInt(string key) => (int)Defined(key).Value.ToInt64();

    /// <summary>A number setting's value.</summary>
    public double GetNumber(string key) => Defined(key).Value.ToDouble();

    /// <summary>A string or choice setting's value.</summary>
    public string GetString(string key) => Defined(key).Value.ToStringValue();

    /// <summary>
    /// Changes a setting. A number outside its range is clamped. <see cref="Changed"/> is raised when the value really is different. A mod can change only its own settings (<c>mod.&lt;its id&gt;.…</c>) and the audio
    /// buses it defined.
    /// </summary>
    /// <param name="key">A defined key.</param>
    /// <param name="value">A value of the setting's type.</param>
    /// <exception cref="ValueException">There is no such setting, the value is of another type or not one of the choices, or a mod changes a setting that is not its own.</exception>
    [Method]
    public void Set(string key, Value value)
    {
        Slot slot = Defined(key);
        RequireOwn(slot);
        Assign(slot, slot.Def!.Coerce(value));
    }

    /// <summary>Puts a setting back to its default.</summary>
    /// <param name="key">A defined key.</param>
    [Method]
    public void Reset(string key)
    {
        Slot slot = Defined(key);
        RequireOwn(slot);
        Assign(slot, slot.Def!.Default);
    }

    /// <summary>Puts the settings whose key starts with <paramref name="prefix"/> back to their defaults (all of them when it is nil). A mod gives the prefix of its own settings (<c>mod.&lt;its id&gt;.</c>).</summary>
    /// <param name="prefix">The start of the keys, or nil for all.</param>
    [Method]
    public void ResetAll(string? prefix = null)
    {
        ScriptOwner? caller = Caller;
        if (caller is { IsMod: true } && (prefix is null || !prefix.StartsWith($"mod.{caller.Id}.", StringComparison.Ordinal)))
        {
            throw NotYours(caller);
        }

        foreach (Slot slot in _slots.Values.Where(s => s.Def is not null && (prefix is null || s.Key.StartsWith(prefix, StringComparison.Ordinal))).OrderBy(s => s.Key, StringComparer.Ordinal).ToList())
        {
            Assign(slot, slot.Def!.Default);
        }
    }

    /// <summary>The defined keys that start with <paramref name="prefix"/> (all when it is nil), sorted.</summary>
    /// <param name="prefix">The start of the keys, or nil.</param>
    [Method]
    public string[] Keys(string? prefix = null) =>
        [.. _slots.Values.Where(s => s.Def is not null && (prefix is null || s.Key.StartsWith(prefix, StringComparison.Ordinal))).Select(s => s.Key).Order(StringComparer.Ordinal)];

    /// <summary>The definition of a key, or null when it is not defined.</summary>
    public SettingDef? Definition(string key) => _slots.TryGetValue(key, out Slot? slot) ? slot.Def : null;

    /// <summary>
    /// What a setting is: <c>{ type, default, min, max, choices, restart, help }</c> (the fields that do not apply are left out), or nil when the key is not defined. What an options menu builds its controls from.
    /// </summary>
    /// <param name="key">A key.</param>
    [Method]
    public ScriptTable? Info(string key)
    {
        if (Definition(key) is not { } def)
        {
            return null;
        }

        return new ScriptRecord()
            .With("type", Value.String(SettingDef.TypeName(def.Type)))
            .With("default", def.Default)
            .With("min", double.IsFinite(def.Min) ? Value.Float(def.Min) : default)
            .With("max", double.IsFinite(def.Max) ? Value.Float(def.Max) : default)
            .With("choices", def.Choices is null ? default : Value.ListOf(def.Choices, Value.String))
            .With("restart", Value.Bool(def.Restart))
            .With("help", Value.String(def.Help));
    }

    private Slot Defined(string key) =>
        _slots.TryGetValue(key, out Slot? slot) && slot.Def is not null ? slot : throw new ValueException($"there is no setting '{key}' (define it with settings:define)");

    private static ScriptOwner? Caller => ScriptCaller.Current as ScriptOwner;

    private static ValueException NotYours(ScriptOwner mod) => new($"a mod can only change its own settings (mod.{mod.Id}.…)");

    /// <summary>A mod changes the settings under <c>mod.&lt;id&gt;.</c> and the ones it defined itself; the engine and the base game change any.</summary>
    private static void RequireOwn(Slot slot)
    {
        if (Caller is { IsMod: true } mod && !slot.Key.StartsWith($"mod.{mod.Id}.", StringComparison.Ordinal) && slot.Def!.Owner != mod.Id)
        {
            throw NotYours(mod);
        }
    }

    private void Assign(Slot slot, Value value)
    {
        if (slot.Value == value)
        {
            return;
        }

        slot.Value = value;
        MarkChanged(slot);
    }

    private void MarkChanged(Slot slot)
    {
        _dirty = true;
        Changed.Emit(slot.Key);
    }

    // ---- Bindings -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The input map whose rebindings are kept: the bindings in the file are applied to it when it is set (those of actions it does not have yet wait until they are added), and every rebinding marks the settings
    /// to be saved.
    /// </summary>
    public InputActionMap? Bindings
    {
        set
        {
            if (_map is not null)
            {
                _savedBindings = new Dictionary<string, string[]>(_map.Save(), StringComparer.Ordinal);
                _map.BindingsChanged -= OnBindingsChanged;
            }

            _map = value;
            if (value is null)
            {
                return;
            }

            foreach (string problem in value.LoadSaved(_savedBindings))
            {
                Log.Warn("Settings", $"saved bindings: {problem}");
            }

            value.BindingsChanged += OnBindingsChanged;
        }
    }

    private void OnBindingsChanged() => _dirty = true;

    protected override void OnDestroyed()
    {
        if (_map is not null)
        {
            _map.BindingsChanged -= OnBindingsChanged;
            _map = null;
        }
    }

    // ---- The file -----------------------------------------------------------------------------------------------------------------

    private void Read()
    {
        if (!_files!.FileExists(_path!))
        {
            return;
        }

        string text;
        try
        {
            text = _files.ReadAllText(_path!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Settings", $"{_path} cannot be read ({e.Message}): the defaults are used.");
            return;
        }

        XmlNode root;
        try
        {
            root = XmlNode.Parse(text, _path!);
            if (root.Name != "settings")
            {
                throw new XmlParseException(_path!, root.Line, $"the root element is <{root.Name}>, not <settings>");
            }
        }
        catch (XmlParseException e)
        {
            string bad = _path!.EndsWith(".xml", StringComparison.Ordinal) ? _path[..^4] + ".bad.xml" : _path + ".bad";
            Log.Warn("Settings", $"{e.Message} The defaults are used, and the file is kept as {bad}; the next save replaces it.");
            try
            {
                _files.WriteAllText(bad, text);
            }
            catch (Exception copyFailed) when (copyFailed is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Settings", $"{bad} was not written: {copyFailed.Message}");
            }

            return;
        }

        if (int.TryParse(root.Attribute("version"), out int version) && version > FormatVersion)
        {
            Log.Warn("Settings", $"{_path} is version {version}, from a newer game: it is read as far as this one understands it.");
        }

        foreach (XmlNode child in root.Children)
        {
            if (child.Name == "value" && child.Attribute("key") is { } key)
            {
                if (SettingDef.IsValidKey(key))
                {
                    if (!_slots.TryGetValue(key, out Slot? slot))
                    {
                        _slots[key] = slot = new Slot(key);
                    }

                    slot.Raw = child.Text;
                }
                else
                {
                    Log.Warn("Settings", $"{_path}: '{key}' is not a setting key and is dropped.");
                }
            }
            else if (child.Name == "bindings")
            {
                foreach (XmlNode action in child.Children.Where(c => c.Name == "action" && c.Attribute("id") is not null))
                {
                    _savedBindings[action.Attribute("id")!] = action.Text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                }
            }
        }
    }

    private string ToXml()
    {
        var root = new XmlNode("settings");
        root.SetAttribute("version", FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (Slot slot in _slots.Values.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            string? text = slot.Def is { } def ? (slot.Value != def.Default ? SettingDef.Format(slot.Value) : null) : slot.Raw;
            if (text is null)
            {
                continue;
            }

            var value = new XmlNode("value") { Text = text };
            value.SetAttribute("key", slot.Key);
            root.Children.Add(value);
        }

        IReadOnlyDictionary<string, string[]> bindings = _map?.Save() ?? _savedBindings;
        if (bindings.Count > 0)
        {
            var node = new XmlNode("bindings");
            foreach ((string id, string[] texts) in bindings.OrderBy(b => b.Key, StringComparer.Ordinal))
            {
                var action = new XmlNode("action") { Text = string.Join(" | ", texts) };
                action.SetAttribute("id", id);
                node.Children.Add(action);
            }

            root.Children.Add(node);
        }

        return root.ToXml();
    }

    /// <summary>
    /// Writes the settings to the file now, as the values that differ from their defaults. The file is written beside itself and moved over the old one, so a crash leaves one of the two whole. A store made with
    /// <c>new()</c> has no file. Returns false (and logs once) when it cannot be written; the changes stay to be saved.
    /// </summary>
    [Method]
    public bool Save()
    {
        _attempted = true;
        _lastAttempt = Clock.GetTimestamp();
        if (_files is null)
        {
            _dirty = false;
            return true;
        }

        string text = ToXml();
        try
        {
            if (text == _lastWritten || (text == EmptyDocument && !_files.FileExists(_path!)))
            {
                _dirty = false;
                return true;
            }

            _files.WriteAllText(_path!, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or VfsException)
        {
            if (!_failing)
            {
                Log.Warn("Settings", $"{_path} was not saved, the old file stays: {e.Message}");
            }

            _failing = true;
            return false;
        }

        _failing = false;
        _lastWritten = text;
        _dirty = false;
        return true;
    }

    /// <summary>
    /// Saves when there are changes and the last attempt to save was at least <paramref name="minimumInterval"/> ago: called every frame, it writes a slider that is being dragged once in that time, and the last
    /// change soon after it.
    /// </summary>
    /// <param name="minimumInterval">The time between two writes.</param>
    public void SaveIfDirty(TimeSpan minimumInterval)
    {
        if (!_dirty || (_attempted && Clock.GetElapsedTime(_lastAttempt) < minimumInterval))
        {
            return;
        }

        _ = Save();
    }

    /// <summary>Saves what is not saved yet, whatever the debounce says: at the end of the session.</summary>
    public void Flush()
    {
        if (_dirty)
        {
            _ = Save();
        }
    }
}
