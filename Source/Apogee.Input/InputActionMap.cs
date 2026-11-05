using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Apogee.Reflection;

namespace Apogee.Input;

/// <summary>One action the player can bind: where it works (its contexts) and what it is bound to (its defaults until the player rebinds it).</summary>
public sealed class InputAction
{
    internal InputAction(string id, string help, string[] contexts, bool analog, Binding[] defaults)
    {
        Id = id;
        Help = help;
        ContextArray = contexts;
        Analog = analog;
        Defaults = defaults;
        Bindings = defaults;
    }

    public string Id { get; }

    public string Help { get; }

    public IReadOnlyList<string> Contexts => ContextArray;

    /// <summary>Read by strength (a stick, a trigger, the mouse's motion, a key as 0 or 1) instead of fired: movement, braking, looking.</summary>
    public bool Analog { get; }

    public IReadOnlyList<Binding> Defaults { get; }

    public IReadOnlyList<Binding> Bindings { get; internal set; }

    internal string[] ContextArray { get; }
}

/// <summary>Two actions that can be active together and are bound to the same input.</summary>
public readonly record struct BindingConflict(string Action, string Other, Binding Binding);

/// <summary>
/// Every action the player can bind, where each works (its contexts) and what it is bound to. Contexts are names the game chooses (<c>world</c>,
/// <c>on_foot</c>, <c>vehicle</c>, <c>menu</c>); situations say which are active together, so two actions that can never be active at once may share an input.
/// A <see cref="InputActionRouter"/> turns the player's input into these actions.
/// </summary>
[ApogeeClass(Creatable = true)]
public sealed partial class InputActionMap : EngineObject
{
    private readonly List<InputAction> _actions = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string[]> _situations = [];

    /// <summary>The actions, in the order they were added.</summary>
    public IReadOnlyList<InputAction> Actions => _actions;

    public InputAction? Find(string id) => _index.TryGetValue(id, out int i) ? _actions[i] : null;

    /// <summary>Says that these contexts are active together (a situation). Without any, two actions clash when they share a context.</summary>
    [Method]
    public void AddSituation(string[] contexts) => _situations.Add([.. contexts]);

    /// <summary>Adds an action, bound to <paramref name="defaults"/> (binding texts: see <see cref="Binding"/>). An analog action is read by strength; the others fire.</summary>
    [Method]
    public void AddAction(string id, string[] contexts, string[] defaults, bool analog = false, string help = "")
    {
        if (_index.ContainsKey(id))
        {
            throw new ValueException($"input action '{id}' is defined twice");
        }

        _index[id] = _actions.Count;
        _actions.Add(new InputAction(id, help, [.. contexts], analog, ParseAll(id, defaults)));
    }

    [Method]
    public bool HasAction(string id) => _index.ContainsKey(id);

    /// <summary>The ids of the actions, in the order they were added.</summary>
    [Method]
    public string[] GetActions() => [.. _actions.Select(a => a.Id)];

    /// <summary>What an action is for, as it was added.</summary>
    [Method]
    public string GetHelp(string id) => Get(id).Help;

    [Method]
    public string[] GetContexts(string id) => [.. Get(id).Contexts];

    [Method]
    public bool IsAnalog(string id) => Get(id).Analog;

    /// <summary>True when an action of <paramref name="a"/> and one of <paramref name="b"/> can be active at the same time.</summary>
    [Method]
    public bool ContextsOverlap(string[] a, string[] b)
    {
        if (_situations.Count == 0)
        {
            return a.Intersect(b).Any();
        }

        return _situations.Any(situation => a.Intersect(situation).Any() && b.Intersect(situation).Any());
    }

    /// <summary>Rebinds an action. Throws (a script error) on a binding that does not read.</summary>
    [Method]
    public void SetBindings(string id, string[] bindings) => Get(id).Bindings = ParseAll(id, bindings);

    [Method]
    public string[] GetBindings(string id) => [.. Get(id).Bindings.Select(b => b.ToString())];

    [Method]
    public string[] GetDefaults(string id) => [.. Get(id).Defaults.Select(b => b.ToString())];

    [Method]
    public void Reset(string id) => Get(id).Bindings = Get(id).Defaults;

    [Method]
    public void ResetAll()
    {
        foreach (InputAction action in _actions)
        {
            action.Bindings = action.Defaults;
        }
    }

    // An analog action reads its input whatever the modifiers and the trigger, so it clashes with anything else on that input; the others clash on the same
    // modifiers and trigger (a press and a hold on one key are told apart by time).
    private static bool Clash(InputAction a, Binding x, InputAction b, Binding y) =>
        x.Input == y.Input && (a.Analog || b.Analog || (x.Modifiers == y.Modifiers && x.Trigger == y.Trigger));

    /// <summary>The actions that can be active together with <paramref name="id"/> and are bound to <paramref name="binding"/> as well.</summary>
    public IReadOnlyList<string> Conflicts(string id, Binding binding)
    {
        InputAction action = Get(id);
        var result = new List<string>();
        foreach (InputAction other in _actions)
        {
            if (other != action && ContextsOverlap(action.ContextArray, other.ContextArray) && other.Bindings.Any(b => Clash(action, binding, other, b)))
            {
                result.Add(other.Id);
            }
        }

        return result;
    }

    /// <summary>The actions that would clash if <paramref name="id"/> were bound to this text.</summary>
    [Method]
    public string[] GetConflicts(string id, string binding) => [.. Conflicts(id, Binding.Parse(binding))];

    /// <summary>Every clash among the current bindings, each pair once.</summary>
    public IReadOnlyList<BindingConflict> AllConflicts()
    {
        var result = new List<BindingConflict>();
        for (int i = 0; i < _actions.Count; i++)
        {
            foreach (Binding binding in _actions[i].Bindings)
            {
                foreach (string other in Conflicts(_actions[i].Id, binding))
                {
                    if (_index[other] > i)
                    {
                        result.Add(new BindingConflict(_actions[i].Id, other, binding));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Every clash as a line of three tab-separated fields: the action, the other action and the binding.</summary>
    [Method]
    public string[] GetAllConflicts() => [.. AllConflicts().Select(c => $"{c.Action}\t{c.Other}\t{c.Binding}")];

    /// <summary>The bindings that differ from the defaults, by action id: what a game stores in the player's settings.</summary>
    public IReadOnlyDictionary<string, string[]> Save() =>
        _actions.Where(a => !a.Bindings.SequenceEqual(a.Defaults)).ToDictionary(a => a.Id, a => a.Bindings.Select(b => b.ToString()).ToArray(), StringComparer.Ordinal);

    /// <summary>Applies what <see cref="Save"/> wrote. Returns what could not be applied (an unknown action, a binding that does not read); the rest is.</summary>
    public string[] Load(IReadOnlyDictionary<string, string[]> saved)
    {
        var errors = new List<string>();
        foreach ((string id, string[] texts) in saved)
        {
            if (!_index.TryGetValue(id, out int i))
            {
                errors.Add($"unknown input action '{id}'");
                continue;
            }

            var bindings = new List<Binding>();
            foreach (string text in texts)
            {
                if (Binding.TryParse(text, out Binding binding, out string error))
                {
                    if (!bindings.Contains(binding))
                    {
                        bindings.Add(binding);
                    }
                }
                else
                {
                    errors.Add($"{id}: {error}");
                }
            }

            _actions[i].Bindings = bindings.ToArray();
        }

        return [.. errors];
    }

    /// <summary><see cref="Save"/> as text, one line per action: <c>lights = Hold L | Joy RB</c> (an action with no binding has nothing after the equals sign).</summary>
    [Method]
    public string SaveText()
    {
        var text = new StringBuilder();
        foreach (InputAction action in _actions.Where(a => !a.Bindings.SequenceEqual(a.Defaults)))
        {
            Binding.AppendLine(text, action.Id, action.Bindings);
        }

        return text.ToString();
    }

    /// <summary>Applies <see cref="SaveText"/>'s text; returns what could not be applied, as <see cref="Load"/> does.</summary>
    [Method]
    public string[] LoadText(string text)
    {
        var saved = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                errors.Add($"'{line}' is not 'action = binding | binding'");
                continue;
            }

            saved[line[..equals].Trim()] = line[(equals + 1)..].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        return [.. errors, .. Load(saved)];
    }

    /// <summary>The binding text as it would be saved: the trigger, the modifiers and the input spelled the way this class writes them. Throws (a script error) on text that does not read.</summary>
    [Method]
    public static string NormalizeBinding(string text) => Binding.Parse(text).ToString();

    /// <summary>Why a binding text does not read, or an empty string when it does.</summary>
    [Method]
    public static string BindingError(string text) => Binding.TryParse(text, out _, out string error) ? "" : error;

    /// <summary>A binding's parts: the input, its modifiers, its trigger. Throws (a script error) on text that does not read.</summary>
    [Method]
    public static (string Input, Modifiers Modifiers, Trigger Trigger) ParseBinding(string text)
    {
        Binding binding = Binding.Parse(text);
        return (binding.Input, binding.Modifiers, binding.Trigger);
    }

    private InputAction Get(string id) => _index.TryGetValue(id, out int i) ? _actions[i] : throw new ValueException($"unknown input action '{id}'");

    private static Binding[] ParseAll(string id, string[] texts)
    {
        var bindings = new List<Binding>();
        foreach (string text in texts)
        {
            if (!Binding.TryParse(text, out Binding binding, out string error))
            {
                throw new ValueException($"input action '{id}': {error}");
            }

            if (!bindings.Contains(binding))
            {
                bindings.Add(binding);
            }
        }

        return [.. bindings];
    }
}
