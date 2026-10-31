using System.Collections.Generic;
using ClangSharp.Interop;

namespace Apogee.Build.Bindings;

public static class ClangExtensions
{
    /// <summary>The direct children of <paramref name="cursor"/>, in source order.</summary>
    public static unsafe List<CXCursor> Children(this CXCursor cursor)
    {
        var children = new List<CXCursor>();
        cursor.VisitChildren(
            (child, _, _) =>
            {
                children.Add(child);
                return CXChildVisitResult.CXChildVisit_Continue;
            },
            default);
        return children;
    }

    /// <summary>The declaration of <paramref name="type"/>, through libclang itself (<c>CXType.Declaration</c> needs libClangSharp's own library).</summary>
    public static CXCursor Decl(this CXType type) => clang.getTypeDeclaration(type);

    // The properties below are backed by libClangSharp in ClangSharp.Interop; these go to libclang directly.
    public static CXCursor Defn(this CXCursor cursor) => clang.getCursorDefinition(cursor);

    public static int ArgumentCount(this CXCursor cursor) => clang.Cursor_getNumArguments(cursor);

    public static CXCursor Argument(this CXCursor cursor, int index) => clang.Cursor_getArgument(cursor, (uint)index);

    public static bool Variadic(this CXCursor cursor) => clang.Cursor_isVariadic(cursor) != 0;

    public static CXType Pointee(this CXType type) => clang.getPointeeType(type);

    public static CXType Element(this CXType type) => clang.getElementType(type);

    public static CXType Modified(this CXType type) => clang.Type_getModifiedType(type);

    /// <summary>
    /// The fields of a record, in order, including the implicit unnamed fields of C11 anonymous structs and unions (which are
    /// not among the cursor's children).
    /// </summary>
    public static unsafe List<CXCursor> Fields(this CXType record)
    {
        var fields = new List<CXCursor>();
        record.VisitFields(
            (field, _) =>
            {
                fields.Add(field);
                return CXVisitorResult.CXVisit_Continue;
            },
            default);
        return fields;
    }
}
