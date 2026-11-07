using System;
using System.Collections.Generic;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.RmlUi;
using Apogee.Reflection;

namespace Apogee.UI;

/// <summary>
/// A data model: a script's table tied to documents. <c>&lt;p data-model="stats"&gt;{{score}}&lt;/p&gt;</c> shows <c>score</c> of the table; <c>data-if</c>, <c>data-for</c>, <c>data-class-*</c>, <c>data-style-*</c> and
/// <c>data-value</c> (which writes back what the player types) use it too, and <c>data-event-click="buy(1)"</c> calls the table's function <c>buy</c>. The table is read when RmlUi needs a value: change
/// it and say so with <see cref="Dirty"/> (or change a top-level field with <see cref="Set"/>), and the elements that show it are updated at the next frame.
/// </summary>
/// <remarks>The top-level fields are bound when the model is opened, so each has to exist then (use <c>false</c>, <c>0</c>, <c>""</c> or <c>{}</c> for a value that comes later). A field keeps its kind: a number stays a number, a list a list.</remarks>
[ApogeeClass]
public sealed partial class UiDataModel : EngineObject
{
    private readonly UiSystem _system;
    private bool _closed;

    internal UiDataModel(UiSystem system, string name, ScriptTable fields)
    {
        _system = system;
        ModelName = name;
        Fields = fields;
    }

    internal ScriptTable Fields { get; }

    internal string ModelName { get; }

    /// <summary>The name documents use in <c>data-model</c>.</summary>
    [Property]
    public string Name => ModelName;

    /// <summary>Whether the model still exists (it is closed with <see cref="Close"/>, when the module that opened it reloads, and when the UI ends).</summary>
    [Property]
    public bool IsOpen => !_closed;

    /// <summary>Says that the field (a top-level one) or, with no name, everything changed: the elements that show it are updated at the next frame. A change inside a list or struct needs the name of the whole.</summary>
    [Method]
    public void Dirty(string? name = null)
    {
        if (!_closed)
        {
            _system.DirtyModel(this, name);
        }
    }

    /// <summary>Sets a top-level field of the table and marks it dirty.</summary>
    [Method]
    public bool Set(string name, Value value)
    {
        if (_closed || !UiSystem.SetModelField(this, name, value))
        {
            return false;
        }

        _system.DirtyModel(this, name);
        return true;
    }

    /// <summary>Whether the field changed since the last frame updated the documents.</summary>
    [Method]
    public bool IsDirty(string name) => !_closed && _system.IsModelDirty(this, name);

    /// <summary>Removes the model. Close the documents that use it first.</summary>
    [Method]
    public void Close()
    {
        if (!_closed)
        {
            _closed = true;
            _system.RemoveModel(this);
            Fields.Dispose();
        }
    }

    protected override void OnDestroyed()
    {
        // Letting go of the handle does not close the model: documents use it.
    }
}

public sealed unsafe partial class UiSystem
{
    // What RmlUi's data variables are: refs to places in a model's table, handed out by Intern and read back by the callbacks below.
    private readonly List<(UiDataModel Model, DataPathSegment[] Path)?> _dataNodes = [null];
    private readonly Dictionary<(ulong Parent, string? Name, int Index), ulong> _dataNodeIds = [];
    private readonly Dictionary<string, UiDataModel> _models = [];
    private readonly Dictionary<ulong, ScriptFunction> _dataEvents = [];
    private ulong _nextDataEvent;
    private byte* _textBuffer;
    private int _textCapacity;

    /// <summary>Opens a data model called <paramref name="name"/> over a table: its fields are what documents with <c>data-model="name"</c> show. Null when there is a model of that name already. Open it before the documents that use it load (a script in the document's head can).</summary>
    [Method]
    public UiDataModel? OpenDataModel(string name, ScriptTable fields)
    {
        if (_models.ContainsKey(name))
        {
            Log.Error("UI", $"there is a data model called '{name}' already");
            return null;
        }

        fixed (byte* n = Utf8.ToZeroTerminated(name))
        {
            if (!Rml.DataModelCreate(Context, n))
            {
                Log.Error("UI", $"data model '{name}' could not be created");
                return null;
            }

            var model = new UiDataModel(this, name, fields);
            _models[name] = model;
            ulong root = Intern(model, 0, default, root: true);
            foreach (string key in fields.KeysAt([]))
            {
                DataPathSegment[] path = [DataPathSegment.Field(key)];
                ScriptDataKind kind = fields.KindAt(path);
                fixed (byte* k = Utf8.ToZeroTerminated(key))
                {
                    switch (kind)
                    {
                        case ScriptDataKind.Scalar or ScriptDataKind.Array or ScriptDataKind.Struct:
                            if (!Rml.DataModelBind(Context, n, k, Intern(model, root, DataPathSegment.Field(key)), (int)kind))
                            {
                                Log.Warn("UI", $"data model '{name}': '{key}' could not be bound");
                            }

                            break;
                        case ScriptDataKind.Function:
                            ulong token = ++_nextDataEvent;
                            _dataEvents[token] = fields.FunctionAt(path)!;
                            Rml.DataModelBindEvent(Context, n, k, token);
                            break;
                    }
                }
            }

            ScriptScope.OnReload(model.Close); // a module that is reloaded opens its models again
            return model;
        }
    }

    private ulong Intern(UiDataModel model, ulong parent, DataPathSegment segment, bool root = false)
    {
        if (root)
        {
            _dataNodes.Add((model, []));
            return (ulong)(_dataNodes.Count - 1);
        }

        (ulong, string?, int) key = (parent, segment.Name, segment.Index);
        if (_dataNodeIds.TryGetValue(key, out ulong known))
        {
            return known;
        }

        (UiDataModel Model, DataPathSegment[] Path) from = _dataNodes[(int)parent]!.Value;
        DataPathSegment[] path = [.. from.Path, segment];
        _dataNodes.Add((model, path));
        ulong id = (ulong)(_dataNodes.Count - 1);
        _dataNodeIds[key] = id;
        return id;
    }

    internal void DirtyModel(UiDataModel model, string? name)
    {
        fixed (byte* m = Utf8.ToZeroTerminated(model.ModelName))
        fixed (byte* n = name is null ? null : Utf8.ToZeroTerminated(name))
        {
            Rml.DataModelDirty(Context, m, n);
        }
    }

    internal bool IsModelDirty(UiDataModel model, string name)
    {
        fixed (byte* m = Utf8.ToZeroTerminated(model.ModelName))
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        {
            return Rml.DataModelIsDirty(Context, m, n);
        }
    }

    internal static bool SetModelField(UiDataModel model, string name, Value value) => model.Fields.SetAt([DataPathSegment.Field(name)], value);

    internal void RemoveModel(UiDataModel model)
    {
        if (!_disposed)
        {
            fixed (byte* m = Utf8.ToZeroTerminated(model.ModelName))
            {
                Rml.DataModelRemove(_context, m);
            }
        }

        _models.Remove(model.ModelName);
        for (int i = 1; i < _dataNodes.Count; i++)
        {
            if (_dataNodes[i] is { } node && ReferenceEquals(node.Model, model))
            {
                _dataNodes[i] = null;
            }
        }

        foreach ((ulong Parent, string? Name, int Index) key in new List<(ulong, string?, int)>(_dataNodeIds.Keys))
        {
            if (_dataNodes[(int)_dataNodeIds[key]] is null)
            {
                _dataNodeIds.Remove(key);
            }
        }
    }

    private void CloseModels()
    {
        foreach (UiDataModel model in new List<UiDataModel>(_models.Values))
        {
            model.Close();
        }

        _dataEvents.Clear();
        if (_textBuffer != null)
        {
            System.Runtime.InteropServices.NativeMemory.Free(_textBuffer);
            _textBuffer = null;
        }
    }

    // ---- Callbacks: RmlUi asks for the value, size or child of a variable ----

    private (UiDataModel Model, DataPathSegment[] Path)? NodeOf(ulong reference) =>
        reference > 0 && reference < (ulong)_dataNodes.Count ? _dataNodes[(int)reference] : null;

    private int GetData(ulong reference, ArVariant* result)
    {
        if (NodeOf(reference) is not { } node)
        {
            return 0;
        }

        Value value = node.Model.Fields.GetAt(node.Path);
        *result = default;
        switch (value.Kind)
        {
            case ValueKind.Bool:
                result->Type = 1;
                result->Integer = value.ToBool() ? 1 : 0;
                break;
            case ValueKind.Int or ValueKind.Float:
                double number = value.ToDouble();
                if (number == Math.Floor(number) && Math.Abs(number) < 9007199254740992.0)
                {
                    result->Type = 2;
                    result->Integer = (long)number;
                }
                else
                {
                    result->Type = 3;
                    result->Number = number;
                }

                break;
            case ValueKind.String:
                result->Type = 4;
                result->Text = TextBuffer(value.ToStringValue());
                break;
        }

        return 1;
    }

    /// <summary>The text as UTF-8 in a buffer that stays valid until the next call: RmlUi copies it as soon as the callback returns.</summary>
    private byte* TextBuffer(string text)
    {
        int needed = System.Text.Encoding.UTF8.GetByteCount(text) + 1;
        if (needed > _textCapacity)
        {
            _textBuffer = (byte*)System.Runtime.InteropServices.NativeMemory.Realloc(_textBuffer, (nuint)needed);
            _textCapacity = needed;
        }

        int written = System.Text.Encoding.UTF8.GetBytes(text, new Span<byte>(_textBuffer, needed - 1));
        _textBuffer[written] = 0;
        return _textBuffer;
    }

    private int SetData(ulong reference, ArVariant* incoming)
    {
        if (NodeOf(reference) is not { } node)
        {
            return 0;
        }

        // What the player typed is text; it becomes the kind the field has.
        Value current = node.Model.Fields.GetAt(node.Path);
        Value value = incoming->Type switch
        {
            1 => Value.Bool(incoming->Integer != 0),
            2 => Value.Float(incoming->Integer),
            3 => Value.Float(incoming->Number),
            4 => Value.String(Utf8.FromPointer(incoming->Text) ?? ""),
            _ => default,
        };

        switch (current.Kind)
        {
            case ValueKind.Int or ValueKind.Float when value.Kind == ValueKind.String:
                if (!double.TryParse(value.ToStringValue(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                {
                    return 0;
                }

                value = Value.Float(parsed);
                break;
            case ValueKind.Bool when value.Kind == ValueKind.String:
                value = Value.Bool(value.ToStringValue() is "true" or "1" or "on");
                break;
            case ValueKind.String when value.Kind is ValueKind.Float or ValueKind.Int or ValueKind.Bool:
                value = Value.String(value.ToString());
                break;
        }

        return node.Model.Fields.SetAt(node.Path, value) ? 1 : 0;
    }

    private int SizeOfData(ulong reference) => NodeOf(reference) is { } node ? node.Model.Fields.LengthAt(node.Path) : 0;

    private ulong ChildOfData(ulong reference, byte* name, int index, int* kind)
    {
        *kind = 0;
        if (NodeOf(reference) is not { } node)
        {
            return 0;
        }

        DataPathSegment segment = name != null ? DataPathSegment.Field(Utf8.FromPointer(name) ?? "") : DataPathSegment.Element(index);
        DataPathSegment[] path = [.. node.Path, segment];
        ScriptDataKind found = node.Model.Fields.KindAt(path);
        if (found is not (ScriptDataKind.Scalar or ScriptDataKind.Array or ScriptDataKind.Struct))
        {
            return 0;
        }

        *kind = (int)found;
        return Intern(node.Model, reference, segment);
    }

    private void DispatchDataEvent(ulong token, ArEvent* native, ArVariant* arguments, int count)
    {
        if (!_dataEvents.TryGetValue(token, out ScriptFunction? function))
        {
            return;
        }

        var wrapper = new UiEvent(this, native);
        try
        {
            var values = new Value[count + 1];
            values[0] = Value.Object(wrapper);
            for (int i = 0; i < count; i++)
            {
                ArVariant a = arguments[i];
                values[i + 1] = a.Type switch
                {
                    1 => Value.Bool(a.Integer != 0),
                    2 => Value.Float(a.Integer),
                    3 => Value.Float(a.Number),
                    4 => Value.String(Utf8.FromPointer(a.Text) ?? ""),
                    _ => default,
                };
            }

            function.Call(values, []);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Log.Error("UI", $"a data event handler failed: {failure.Message}");
        }
        finally
        {
            wrapper.Invalidate();
            wrapper.Destroy();
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int HostDataGet(void* user, ulong reference, ArVariant* value)
    {
        try
        {
            return s_active?.GetData(reference, value) ?? 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("UI", $"reading a data model failed: {e.Message}");
            return 0;
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int HostDataSet(void* user, ulong reference, ArVariant* value)
    {
        try
        {
            return s_active?.SetData(reference, value) ?? 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("UI", $"writing a data model failed: {e.Message}");
            return 0;
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int HostDataSize(void* user, ulong reference)
    {
        try
        {
            return s_active?.SizeOfData(reference) ?? 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("UI", $"reading a data model failed: {e.Message}");
            return 0;
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static ulong HostDataChild(void* user, ulong reference, byte* name, int index, int* kind)
    {
        *kind = 0;
        try
        {
            return s_active?.ChildOfData(reference, name, index, kind) ?? 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("UI", $"reading a data model failed: {e.Message}");
            return 0;
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void HostDataEvent(void* user, ulong token, ArEvent* native, ArVariant* arguments, int count) => s_active?.DispatchDataEvent(token, native, arguments, count);
}
