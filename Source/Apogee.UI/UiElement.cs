using System;
using System.Numerics;
using Apogee.Interop;
using Apogee.Native.RmlUi;
using Apogee.Reflection;

namespace Apogee.UI;

/// <summary>
/// An element of a document (a <c>div</c>, a text node, a whole document) as scripts hold it. The object is a handle: it names the element without keeping it alive, so one whose element has been destroyed
/// (its document was closed, an ancestor was destroyed) is <see cref="IsAlive"/> false and every call on it does nothing or answers "none". An element made with <see cref="CreateElement"/> or
/// <see cref="CreateText"/> belongs to the object that holds it until it is put in a tree with <see cref="Append"/> or <see cref="InsertBefore"/>; an element nobody ever put in a tree is destroyed
/// when the script lets go of it, and one in a tree lives as long as the tree does. Elements are made, moved and changed here.
/// </summary>
[ApogeeClass]
public sealed unsafe partial class UiElement : EngineObject
{
    private readonly UiSystem _system;

    /// <summary>The document that made this element, as a handle of its own (0 for one that came out of a tree): an element in no tree has no document in RmlUi, but still has to be able to make more elements.</summary>
    private readonly ulong _origin;

    internal UiElement(UiSystem system, ulong handle, ulong origin = 0)
    {
        _system = system;
        Native = handle;
        _origin = origin;
    }

    /// <summary>The handle of the C API (<see cref="EngineObject.Handle"/> is the script's).</summary>
    internal ulong Native { get; }

    private UiElement? Wrap(ulong handle) => handle == 0 ? null : new UiElement(_system, handle);

    /// <summary>Whether the element still exists.</summary>
    [Property]
    public bool IsAlive => !IsDestroyed && Rml.ElementIsAlive(Native);

    /// <summary>The tag (<c>div</c>, <c>body</c>), or empty for a dead element.</summary>
    [Property]
    public string Tag => UiSystem.ElementText(Native, static (e, b, c) => Rml.ElementTag(e, b, c));

    /// <summary>The element's id (the <c>id</c> attribute; ids are unique in a document).</summary>
    [Property]
    public string Id
    {
        get => UiSystem.ElementText(Native, static (e, b, c) => Rml.ElementGetId(e, b, c));
        set
        {
            fixed (byte* p = Utf8.ToZeroTerminated(value))
            {
                Rml.ElementSetId(Native, p);
            }
        }
    }

    /// <summary>The classes, separated by spaces: what the style rules (<c>.card</c>) match.</summary>
    [Property]
    public string ClassName
    {
        get => UiSystem.ElementText(Native, static (e, b, c) => Rml.ElementGetClassNames(e, b, c));
        set
        {
            fixed (byte* p = Utf8.ToZeroTerminated(value))
            {
                Rml.ElementSetClassNames(Native, p);
            }
        }
    }

    /// <summary>The RML of what is inside the element; setting it replaces the children.</summary>
    [Property]
    public string InnerRml
    {
        get => UiSystem.ElementText(Native, static (e, b, c) => Rml.ElementGetInnerRml(e, b, c));
        set
        {
            fixed (byte* p = Utf8.ToZeroTerminated(value))
            {
                Rml.ElementSetInnerRml(Native, p);
            }
        }
    }

    /// <summary>The text of a text node (made with <see cref="CreateText"/>); empty for any other element.</summary>
    [Property]
    public string Text
    {
        get => UiSystem.ElementText(Native, static (e, b, c) => Rml.TextGet(e, b, c));
        set
        {
            fixed (byte* p = Utf8.ToZeroTerminated(value))
            {
                Rml.TextSet(Native, p);
            }
        }
    }

    /// <summary>Whether it can be seen: not <c>display: none</c> or <c>visibility: hidden</c>, itself or any element above it.</summary>
    [Property]
    public bool IsVisible => Rml.ElementIsVisible(Native);

    /// <summary>The element it is in, or nothing for a document or an element in no tree.</summary>
    [Property]
    public UiElement? Parent => Wrap(Rml.ElementParent(Native));

    /// <summary>The number of elements and text nodes directly inside.</summary>
    [Property]
    public int ChildCount => Math.Max(Rml.ElementChildCount(Native), 0);

    /// <summary>The document the element is in (a document is its own).</summary>
    [Property]
    public UiElement? Document => Wrap(DocumentHandle());

    /// <summary>A new handle to the document the element is in, or, for one in no tree, the one that made it; the caller owns it.</summary>
    private ulong DocumentHandle()
    {
        ulong document = Rml.ElementDocument(Native);
        return document != 0 || _origin == 0 ? document : Rml.ElementDocument(_origin);
    }

    // ---- Documents ----

    /// <summary>Makes the document visible (and it starts to be drawn and to take input).</summary>
    [Method]
    public void Show() => Rml.DocumentShow(Native);

    [Method]
    public void Hide() => Rml.DocumentHide(Native);

    /// <summary>Unloads the document: everything in it is destroyed. Call it on the document.</summary>
    [Method]
    public void Close() => _system.CloseDocument(this);

    /// <summary>Adds style rules (RCSS) to the document this element is in, on top of its own.</summary>
    [Method]
    public bool AddStyle(string rcss)
    {
        ulong document = DocumentHandle();
        try
        {
            fixed (byte* p = Utf8.ToZeroTerminated(rcss))
            {
                return Rml.DocumentAddStyle(document, p, false);
            }
        }
        finally
        {
            Rml.ElementRelease(document);
        }
    }

    // ---- Making and moving elements ----

    /// <summary>A new element with the tag (<c>div</c>, <c>span</c>, <c>input</c>...) in this element's document, in no tree yet.</summary>
    [Method]
    public UiElement? CreateElement(string tag)
    {
        ulong document = DocumentHandle();
        ulong made;
        fixed (byte* p = Utf8.ToZeroTerminated(tag))
        {
            made = Rml.DocumentCreateElement(document, p);
        }

        return Made(made, document);
    }

    /// <summary>The new element's object, which keeps <paramref name="document"/> (a handle it now owns) to make more elements with while it is in no tree.</summary>
    private UiElement? Made(ulong made, ulong document)
    {
        if (made == 0)
        {
            Rml.ElementRelease(document);
            return null;
        }

        return new UiElement(_system, made, document);
    }

    /// <summary>A new text node in this element's document, in no tree yet.</summary>
    [Method]
    public UiElement? CreateText(string text)
    {
        ulong document = DocumentHandle();
        ulong made;
        fixed (byte* p = Utf8.ToZeroTerminated(text))
        {
            made = Rml.DocumentCreateText(document, p);
        }

        return Made(made, document);
    }

    /// <summary>Puts <paramref name="child"/>, made by <see cref="CreateElement"/> or taken out by <see cref="Remove"/>, last in this element, which owns it from then on. False if it is already in a tree.</summary>
    [Method]
    public bool Append(UiElement child) => Rml.ElementAppendChild(Native, child.Native);

    /// <summary>The same, before <paramref name="before"/>, a child of this element (or last, when it is nothing).</summary>
    [Method]
    public bool InsertBefore(UiElement child, UiElement? before = null) => Rml.ElementInsertBefore(Native, child.Native, before?.Native ?? 0);

    /// <summary>Takes a child out of this element. It is the script's again: put it somewhere else, or let go of it to destroy it.</summary>
    [Method]
    public bool Remove(UiElement child) => Rml.ElementRemoveChild(Native, child.Native);

    /// <summary>Destroys the element with everything inside it, out of its tree. Every object that names it is dead afterwards.</summary>
    [Method]
    public void DestroyElement() => Rml.ElementDestroy(Native);

    [Method]
    public UiElement? Child(int index) => Wrap(Rml.ElementChild(Native, index));

    // ---- Finding ----

    /// <summary>The element with this id in or below this one.</summary>
    [Method]
    public UiElement? ById(string id)
    {
        fixed (byte* p = Utf8.ToZeroTerminated(id))
        {
            return Wrap(Rml.ElementById(Native, p));
        }
    }

    /// <summary>The nearest element, this one included and going up, that a selector (<c>.card</c>, <c>#menu</c>, <c>div.row</c>) matches.</summary>
    [Method]
    public UiElement? Closest(string selector)
    {
        fixed (byte* p = Utf8.ToZeroTerminated(selector))
        {
            return Wrap(Rml.ElementClosest(Native, p));
        }
    }

    /// <summary>Whether <paramref name="other"/> is this element or inside it.</summary>
    [Method]
    public bool Contains(UiElement other) => Rml.ElementContains(Native, other.Native);

    // ---- Attributes and style ----

    [Method]
    public string? GetAttribute(string name)
    {
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        {
            byte* pin = n;
            return UiSystem.ElementTextOrNull(Native, (e, b, c) => Rml.ElementGetAttribute(e, pin, b, c));
        }
    }

    [Method]
    public bool HasAttribute(string name)
    {
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        {
            return Rml.ElementHasAttribute(Native, n);
        }
    }

    [Method]
    public void SetAttribute(string name, string value)
    {
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        fixed (byte* v = Utf8.ToZeroTerminated(value))
        {
            Rml.ElementSetAttribute(Native, n, v);
        }
    }

    [Method]
    public void RemoveAttribute(string name)
    {
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        {
            Rml.ElementRemoveAttribute(Native, n);
        }
    }

    /// <summary>Sets an inline style (<c>set_property("width", "120px")</c>); false when the name or the value is not valid RCSS.</summary>
    [Method]
    public bool SetProperty(string name, string value)
    {
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        fixed (byte* v = Utf8.ToZeroTerminated(value))
        {
            return Rml.ElementSetProperty(Native, n, v);
        }
    }

    [Method]
    public void RemoveProperty(string name)
    {
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        {
            Rml.ElementRemoveProperty(Native, n);
        }
    }

    [Method]
    public void SetPseudoClass(string name, bool on)
    {
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        {
            Rml.ElementSetPseudoClass(Native, n, on);
        }
    }

    [Method]
    public bool HasPseudoClass(string name)
    {
        fixed (byte* n = Utf8.ToZeroTerminated(name))
        {
            return Rml.ElementIsPseudoClassSet(Native, n);
        }
    }

    // ---- State ----

    [Method]
    public bool Focus() => Rml.ElementFocus(Native);

    [Method]
    public void Blur() => Rml.ElementBlur(Native);

    /// <summary>Clicks the element as if the pointer had.</summary>
    [Method]
    public void Click() => Rml.ElementClick(Native);

    [Method]
    public void ScrollIntoView(bool alignWithTop = true) => Rml.ElementScrollIntoView(Native, alignWithTop);

    /// <summary>The element's border box on the screen in pixels, as the last update laid it out: <c>x, y, width, height</c>.</summary>
    [Method]
    public (float X, float Y, float Width, float Height) Rect()
    {
        ArRect rect;
        return Rml.ElementGetRect(Native, &rect) ? (rect.X, rect.Y, rect.Width, rect.Height) : default;
    }

    // ---- Events ----

    /// <summary>
    /// Calls <paramref name="handler"/> with a <see cref="UiEvent"/> whenever an event of this type (<c>click</c>, <c>mousedown</c>, <c>keydown</c>, <c>change</c>, <c>focus</c>...) reaches the element on its way up from
    /// what was hit, or, with <paramref name="capture"/>, on its way down. Returns a number for <see cref="UiSystem.Unlisten"/>. The listener goes when the element does.
    /// </summary>
    [Method]
    public long Listen(string type, ScriptFunction handler, bool capture = false)
    {
        ulong token = _system.Listen(this, type, capture, handler);
        if (token != 0)
        {
            ScriptScope.OnReload(() => _system.Unlisten((long)token)); // a module that is reloaded listens again
        }

        return (long)token;
    }

    /// <summary>Takes back a listener of this element (the number <see cref="Listen"/> returned); the same as <see cref="UiSystem.Unlisten"/>.</summary>
    [Method]
    public bool Unlisten(long token) => _system.Unlisten(token);

    protected override void OnDestroyed()
    {
        // An element that was only ever the script's is destroyed with its last handle; one in a tree stays.
        Rml.ElementRelease(Native);
        if (_origin != 0)
        {
            Rml.ElementRelease(_origin);
        }
    }
}
