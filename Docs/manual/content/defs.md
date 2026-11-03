# Definitions and schemas

A game's data is XML: *definitions* (a crate, a machine, a crop) that mods can add to, change and remove, checked against *schemas* (XSD) that say what a valid one looks
like. `Apogee.Content` reads both (`Apogee.Content.Xml`) and gives code and scripts typed data, with the line of every mistake.

## Definitions

A definition is an element with an `id`. Its name is its *type*; type and id together identify it.

```xml
<!-- res://data/crates.xml: one definition, or a list of them under any root -->
<Crates>
  <Crate id="wooden" weight="12.5">
    <Tag>fragile</Tag>
    <Tag>stackable</Tag>
  </Crate>
  <Crate id="steel" weight="80"/>
</Crates>
```

A file holds one definition (its root has an `id`) or a list (the root's children are the definitions). `DefDatabase.Load(files, folders, schemas)` reads the `.xml` files of the
folders you give it, **in load order**: the folders in order, in each folder its files sorted by name and then its subfolders the same way. In a game the folders come from the
packages (base game first, then mods in dependency order).

What a later file may do with a definition:

| Written | Effect |
| --- | --- |
| `<Crate id="x" …/>` again | **replaces** it (it keeps its place in the list), with a warning that names both files |
| `<Crate id="x" patch="true" …/>` | **patches** it: see below |
| `<Crate id="x" remove="true"/>` | **removes** it |

A patch or a removal of a definition that is not there is a warning, and does nothing. A patch merges into the definition (`DefDatabase.Merge`): its attributes replace the
definition's (and add new ones), its text replaces the text, and its children are matched with the definition's children by name and `id`, or, with no `id`, by name and place
(the second `<Tag/>` of a patch is the second `<Tag>` of the definition, even when the patch removed the first). A matched child is merged the same way; a child with
`remove="true"` takes its match away; a child that matches nothing is added at the end. The `patch` and `remove` attributes are not kept.

```xml
<Crate id="wooden" patch="true" weight="14"><Tag remove="true"/><Tag append="true">reinforced</Tag></Crate>
```

Every `Def` knows `Type`, `Id`, its merged `Node`, its decoded `Value`, and `Sources`: the file that defined it and each file that patched it (with package, path, line), so a mod
that patches a definition can find the files beside the original. Comments are not read.

## Problems

Everything wrong goes in `database.Problems`, each with a severity, the file and line, a path inside the definition, and a message:

```text
mod://base/data/crates.xml:2: Crate.weight (wooden): 'heavy' is not a number
mod://base/data/crates.xml:4: Crate (steel): 'colour' is not an attribute of <Crate> (it has: id, weight).
mod://mod/data/crates.xml:1: warning: Crate: replaces Crate 'wooden' defined in mod://base/data/crates.xml (package 'base').
```

*Errors* are content that does not match its schema, a file that is not well-formed XML, a definition without an `id`. *Warnings* are things that load but are odd: a replacement,
a patch of nothing, a type with no schema. A definition with errors is still in the database, with what could be made of it (a value that does not convert stays as text), so one
mistake does not take a whole game down; the caller decides what to do with `HasErrors`. A problem in a patched definition names the file of the line it is on.

## Schemas

`SchemaSet` loads `.xsd` files (`LoadFile`, `LoadFolder`, `LoadText`), with the files they `xs:include` (found beside them, read once). A package brings the schema of the kinds of
definitions it adds. The engine reads the part of XSD that content needs, and says so when a schema asks for more (`<xs:key> is not supported`, with the file and line), so a schema
never means less than it says:

- `element`, `attribute` (`type`, `default`, `fixed`, `use`; `minOccurs`, `maxOccurs`; elements may use `ref`), `complexType` with `sequence`, `choice`, `all`, `group`, `attributeGroup`,
  `complexContent` and `simpleContent` extensions, `any`, `anyAttribute`;
- `simpleType` restrictions with `enumeration`, `minInclusive`, `maxInclusive`, `minExclusive`, `maxExclusive`, `pattern` (a regular expression that must match all of the text,
  with a time limit), `minLength`, `maxLength`, `length`, and `list`;
- the built-in types: strings (`string`, `token`, `ID`, `date`…), `boolean`, `decimal`, `float`, `double`, and the integers with their ranges (`int`, `long`, `byte`,
  `unsignedByte`, `positiveInteger`…), and the engine's own `vector3` (three numbers, `"1 2.5 -3"` or `"1, 2.5, -3"`, decoded as a `DefVector` and a `vector` in Luau);
- `annotation` with `documentation` (kept, for tools) and `appinfo` (the two conventions below). There are no namespaces: names are plain.

**Order is not checked.** Children may come in any order; each element is checked for how often it appears. The members of a `choice` have no minimum (any one may be the one
chosen), and a repeated `choice` stands for "any order".

### What a definition decodes to (`SchemaSet.Decode`)

An element with a **complex type** is a record: its attributes, typed (numbers, booleans, text; a list type is a list), with the **defaults** of the attributes it leaves out, and its
child elements by name. A child that may appear more than once is a **list**. An element that holds only one repeatable element (`<Units><Unit/><Unit/></Units>`) *is* that list. An
element with a **simple type** is its value. `simpleContent` (text with attributes) is a record with the attributes and `value`, or just the value if it has no attributes. The
example above decodes to:

```text
{id: "wooden", weight: 12.5, Tag: ["fragile", "stackable"]}
```

Numbers are doubles, so whole numbers beyond 2^53 lose precision. Two conventions, written in the declaration's `xs:annotation/xs:appinfo`:

- **`map`**: a container of `<entry key="…">` items is a record by the keys (`<Prices><entry key="wheat" value="2.5"/></Prices>` is `{wheat: 2.5}`); an entry with nothing but `key`
  and `value` gives the value, any other entry is its record without the `key`. A key used twice is a problem.
- **`defaults`**: an element that is left out is decoded as an empty one, so the defaults of its attributes apply, and a list that is left out is empty (without it a missing
  element or list is simply not there).

An **open element** (one with `xs:any`) is how a package adds a kind of child to another package's definition. Each child is checked against the global element named
`<open element>.<child>` when the schemas have one (`<components><motor power="90"/></components>` is described by an element named `components.motor`), and kept as it is otherwise:
attributes as text, children by name (a repeated name is a list), text as `value`. `xs:anyAttribute` keeps unknown attributes as text.

`patch`, `remove`, `append`, `xmlns` and `xsi:…` attributes are accepted on any element. An element the schemas have no declaration for decodes the same generic way, with no problems
(`DecodeResult.Declared` is false), which is how a database without schemas reads everything.

## From Luau

Two global libraries (hand-written natives, declared in `apogee.d.luau`):

```lua
-- defs: the game's definitions, set with LuauVm.Defs (a game sets it when it has loaded its content)
local crate = defs.get("Crate", "wooden")          -- a table, or nil: crate.weight, crate.Tag[1]
for _, c in defs.all("Crate") do print(c.id) end   -- in load order
defs.types()                                       -- {"Barrel", "Crate"}
for _, s in defs.sources("Crate", "wooden") do print(s.package, s.path, s.line, s.role) end   -- "definition", "replacement", "patch"
for _, p in defs.problems() do print(p.severity, p.file, p.line, p.message) end

-- xml: plain XML text to and from tables
local node = xml.decode('<Crate id="a"><Tag>x</Tag></Crate>')   -- { name, attrs, order, children, text, line }
local text = xml.encode(node)
```

A definition's table is made the first time it is asked for and is **read-only all the way down** (assigning to it is an error), and it is the same table for every script, so scripts
cannot change each other's view of the game; copy what you need to change. Setting `LuauVm.Defs` again (a reload) makes later calls see the new definitions, and tables that scripts
already hold stay as they were. Without a database the library is there and finds nothing. `xml.encode` writes attributes in the order of `order` and then the rest sorted by name,
so the text is the same every time; elements nest at most 128 deep, both ways.

## Tests


`Tests/Apogee.Content.Tests`: the XML reader and writer (lines, entities, a DTD refused), each schema feature with the exact problem messages, the merge rules including place counting
and removal, and the database (load order, replace, patch, remove, sources, problems with file and line, kinds without a schema). `Tests/Apogee.Scripting.Tests` runs `xml` and `defs` from scripts
(read-only tables, identity, sources and problems, reloads), and `Tests/Luau/xml.test.luau` runs them in the NativeAOT binary.
