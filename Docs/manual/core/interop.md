# Interop

`Apogee.Interop` is what the generated bindings stand on.

## NativeLibraries

Generated `DllImport`s name a logical library (`SDL3`, `flecs`). Each binding assembly registers the file names to try
per operating system (`NativeLibraries.Register`, called from the generated `*.Library.g.cs`). Resolution tries, per
candidate: next to the executable, then `runtimes/<rid>/native`, then the system search. `TryLoad` lets a test or a tool
ask whether a library is installed without failing.

## CBool

`CBool` is a one-byte C `bool`/`uint8_t` flag with implicit conversions to and from `bool`, so signatures stay blittable
(a managed `bool` is not).

## Utf8

`Utf8.ToZeroTerminated(string)` gives the bytes of a C string (pin them with `fixed` to pass them to native code) and
`Utf8.FromPointer(byte*)` reads one back (null stays null).

```csharp
fixed (byte* name = Utf8.ToZeroTerminated("Apogee"))
{
    SDL.SetWindowTitle(window, name);
}
string? error = Utf8.FromPointer(SDL.GetError());
```
