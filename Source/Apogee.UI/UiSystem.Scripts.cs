using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Apogee.Content.FileSystem;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.RmlUi;
using Apogee.Reflection;

namespace Apogee.UI;

// Luau in documents: <script>, <script src>, onclick="..." and the other on* attributes. A document gets a ScriptEnvironment when its first script or handler appears: a table of globals that falls
// back to the globals of the script that loaded the document, so the owner's limits and file rules apply and what a <script> defines is there for the document's handlers.
public sealed unsafe partial class UiSystem
{
    private readonly Dictionary<ulong, ScriptEnvironment> _documentScripts = [];
    private readonly Dictionary<ulong, object?> _inlineCallers = [];
    private bool _warnedNoScripts;

    /// <summary>The scripting layer that runs the code in documents. Without it a <c>&lt;script&gt;</c> or an <c>onclick</c> is ignored, with a warning.</summary>
    public IScriptEnvironments? ScriptEnvironments { get; set; }

    /// <summary>The number of documents that have scripts running (they have an environment of their own).</summary>
    public int ScriptedDocuments => _documentScripts.Count;

    /// <summary>The environment of a document, made when it is first needed. The caller keeps <paramref name="documentHandle"/> (the environment takes a handle of its own for <c>document</c>) and releases it.</summary>
    private ScriptEnvironment? EnvironmentOf(ulong documentId, ulong documentHandle)
    {
        if (_documentScripts.TryGetValue(documentId, out ScriptEnvironment? known))
        {
            return known;
        }

        if (ScriptEnvironments is null)
        {
            if (!_warnedNoScripts)
            {
                _warnedNoScripts = true;
                Log.Warn("UI", "a document has scripts, but there is no scripting layer here: they are ignored");
            }

            return null;
        }

        ScriptEnvironment environment = ScriptEnvironments.Create();
        environment.SetGlobal("document", Value.Object(new UiElement(this, Rml.ElementDocument(documentHandle))));
        _documentScripts[documentId] = environment;
        return environment;
    }

    private void RunInlineScript(ulong documentId, ulong documentHandle, string source, string content, int line)
    {
        try
        {
            if (EnvironmentOf(documentId, documentHandle) is not { } environment)
            {
                return;
            }

            // The lines before the script are blank, so that an error says the line of the document.
            environment.Run("=" + source, new string('\n', Math.Max(line - 1, 0)) + content);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Log.Error("UI", $"a script in {source} failed: {failure.Message}");
        }
    }

    private void RunExternalScript(ulong documentId, ulong documentHandle, string path)
    {
        if (EnvironmentOf(documentId, documentHandle) is not { } environment)
        {
            return;
        }

        // RmlUi writes the colon of a scheme as '|' in the paths it resolves.
        path = path.Replace('|', ':');
        if (!VfsPath.TryParse(path, out string normalized, out string? problem))
        {
            Log.Error("UI", $"the script {path} is not a valid path ({problem})");
            return;
        }

        if (!environment.CanRead(normalized))
        {
            Log.Error("UI", $"the script {normalized} is not one this document's package may read");
            return;
        }

        string text;
        try
        {
            text = _files.ReadAllText(normalized);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("UI", $"the script {normalized} cannot be read: {e.Message}");
            return;
        }

        try
        {
            environment.Run("@" + normalized, text);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Log.Error("UI", $"the script {normalized} failed: {failure.Message}");
        }
    }

    private bool CompileInlineHandler(ulong documentId, ulong documentHandle, ulong token, string code)
    {
        if (EnvironmentOf(documentId, documentHandle) is not { } environment)
        {
            return false;
        }

        // The attribute runs as whoever wrote it: the script that loaded the document, or another one that put an onclick in the document's elements with inner_rml or set_attribute. The second does not get the
        // document's globals (and with them the first one's rights): it has an environment of its own, as itself.
        _inlineCallers.Remove(token, out object? writer);
        ScriptEnvironment? own = null;
        try
        {
            if (writer is not null && !ReferenceEquals(writer, environment.Owner) && ScriptEnvironments is { } environments)
            {
                using (ScriptCaller.Enter(writer))
                {
                    own = environments.Create();
                }

                own.SetGlobal("document", Value.Object(new UiElement(this, Rml.ElementDocument(documentHandle))));
                environment = own;
            }

            // `event` is the parameter, `element` the element the handler is on, `document` a global of the document.
            ScriptFunction function = environment.Compile("=handler", "event", "local element = event.current; " + code);
            _listeners[token] = new Listener(function, "inline");
            return true;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Log.Error("UI", $"an event attribute does not compile: {failure.Message}");
            return false;
        }
        finally
        {
            own?.Dispose(); // the function keeps the environment's table alive
        }
    }

    private void CloseDocumentScripts(ulong documentId)
    {
        if (_documentScripts.Remove(documentId, out ScriptEnvironment? environment))
        {
            environment.Dispose();
        }
    }

    private void DisposeScripts()
    {
        foreach (ScriptEnvironment environment in _documentScripts.Values)
        {
            environment.Dispose();
        }

        _documentScripts.Clear();
    }

    // RmlUi gives each call a new handle for the document, which is the host's to release.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostLoadInlineScript(void* user, ulong documentId, ulong document, byte* source, byte* content, int line)
    {
        try
        {
            s_active?.RunInlineScript(documentId, document, Utf8.FromPointer(source) ?? "", Utf8.FromPointer(content) ?? "", line);
        }
        finally
        {
            Rml.ElementRelease(document);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostLoadExternalScript(void* user, ulong documentId, ulong document, byte* path)
    {
        try
        {
            s_active?.RunExternalScript(documentId, document, Utf8.FromPointer(path) ?? "");
        }
        finally
        {
            Rml.ElementRelease(document);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HostCompileInlineHandler(void* user, ulong documentId, ulong document, ulong token, byte* code)
    {
        try
        {
            return s_active is { } ui && ui.CompileInlineHandler(documentId, document, token, Utf8.FromPointer(code) ?? "") ? 1 : 0;
        }
        finally
        {
            Rml.ElementRelease(document);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostInstanceInlineHandler(void* user, ulong token)
    {
        if (s_active is { } ui)
        {
            ui._inlineCallers[token] = ScriptCaller.Current;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HostDocumentClosed(void* user, ulong documentId) => s_active?.CloseDocumentScripts(documentId);
}
