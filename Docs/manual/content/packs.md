# Packs

A pack is one file (or a few) that holds a game's content: compressed with zstd, optionally encrypted with AES-256-GCM, and mounted in the [file system](content.md#file-system) like a
folder. A shipped game's `res://` is its packs; during development it is a folder. Mods are not packs: they stay folders or zips so that players can make them without tools.

## Making one

```sh
./build.sh pack Samples/Mods/res Bin/data/game.pack                      # every file under the folder (Apogee.Runtime --pack, with .env loaded)
./build.sh pack Content Bin/data/game.pack --split-mb 1024 --level 15      # parts of about 1 GiB, a smaller (slower) compression
./build.sh pack Content Bin/data/patch.pack --base Bin/data/game.pack      # a patch pack
```

`./build.sh pack` runs `Apogee.Runtime --pack` (the engine's host program has the content library and the native libraries, and the editor and the export step use the same `PackWriter`);
the wrapper loads `.env`, so `APOGEE_PACK_KEY` applies. Run `Apogee.Runtime --pack <folder> <out.pack>` yourself to pack without the wrapper. Paths in the pack are relative to the folder (`packages/core/mod.xml`); files and folders whose name starts with a dot are left out; every name must be valid in a virtual path (no `..`,
no `nul`…), and the tool says which are not. From code: `PackWriter` (`Add`, `AddFile`, `Remove`, `Finish`).

- **Compression.** Each file is compressed with zstd when it has 64 bytes or more and that saves at least 10%; the rest (PNG, KTX2, OGG…) is stored as it is. `--level` is zstd's 1 to 22
  (default 12). Large files are compressed on several threads.
- **Splitting.** A pack grows to `--split-mb` (default 1536) and then continues in the next part: `game.pack`, `game_001.pack`, `game_002.pack`… A file is never split, so one that is
  bigger than the limit gets a part to itself. Opening the first part finds the others; a part that is missing is an error that says which.
- **Patch packs.** With `--base` the packs you name (in order, with the same key) are what the game has already: the new pack holds only the files that are new or different, and a *removal*
  for each file that is gone. Mounted over the base, it replaces, adds and removes.

## Using one

```csharp
PackKey? key = PackKey.FromEnvironment();                    // null when APOGEE_PACK_KEY is not set
using var pack = PackSource.Open("data/game.pack", key);     // the key is ignored for a pack that is not encrypted
files.Mount("res://", pack);                                  // later mounts override earlier ones: a patch pack goes after
```

`Apogee.Runtime --game <folder>` mounts the folder as `res://` and then every `*.pack` in `<folder>/data`, by name (so `patch.pack` goes over `game.pack`), with the key from the environment,
before it looks for packages. Reads are safe from several threads; a file is read, decrypted, decompressed and checked when it is asked for, a file that is neither compressed nor encrypted
is streamed without being read whole (and without its hash checked).

## Encryption and the key

If `APOGEE_PACK_KEY` is set (64 hexadecimal digits: 32 bytes) the pack is encrypted; if it is not, it is not. **No key is set up yet**: nothing in the repository or the CI has one, so packs
are plain. When you want one, put it in `.env` (which git ignores and the `./build.sh` wrapper loads), make the same key available to everything that makes or opens packs (the editor, the
tools, the shipped game, a CI secret), and never commit it or print it (a `PackKey` writes itself as `PackKey(…)`). A pack made with one key cannot be read with another, and changing the
key means making the packs again.

What it does, in detail (`PackFormat` has the layout):

- Each part has a random 32-byte **salt**; the key that seals a part is `HKDF-SHA256(key, salt)`, so the pack key never encrypts anything directly and two packs of the same files differ.
- Every file and the index is its own **AES-256-GCM** blob (`nonce | tag | ciphertext`, a random nonce each time). A wrong key, or a single changed byte, fails the check instead of producing
  garbage. The file's **path is authenticated** with it, so a file cannot be moved to another path in the pack.
- The first four bytes of the pack are `APGK` without a key, and with one a few bytes of a hash of the key: an encrypted pack has no signature to search for, and opening one with the
  wrong key, or with none, is an error that says so (without the key in it).
- Compression comes first and encryption after it (encrypted data does not compress). Without a key, damage is found by the zstd checksum of a compressed file and by a hash (the first 16
  bytes of the SHA-256 of the file) that is checked for every file read whole.

AES-GCM comes from the platform: Windows and macOS have it, and on Linux it needs OpenSSL (`libssl`), which desktops and Steam's runtime have.

**What it is not.** A game has to be able to open its own packs, so the key is in the game; a determined person can get it out. The encryption keeps the content from being taken apart
casually and makes a changed pack fail to load. It is not copy protection.

## Tests

`Tests/Apogee.Content.Tests`: the key, a round trip of every kind of file (empty, text, noise, nested), compression only where it pays, mounting, streaming, encryption (the content and the
magic are not in the file, a salt per pack, no key and a wrong key), tampering (a flipped byte in a file or the index of a sealed pack, in a stored and in a compressed file of a plain one,
a truncated file, a part missing), split parts (also a file bigger than a part, and sealed), patch packs, path rules, and many threads reading at once. `Tests/Apogee.Runtime.Tests` runs
a game from a pack with a patch pack over it, and an encrypted game with the key from the environment.
