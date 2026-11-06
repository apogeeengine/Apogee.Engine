using System;
using System.Collections.Generic;
using System.IO;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Steam;
using Apogee.Reflection;
using SteamApi = Apogee.Native.Steam.Steam;

namespace Apogee.Steam;

// Workshop items are the game's mods: each is a mod folder (a mod.xml at its root) that Steam downloads and keeps up to date. The game mounts the installed ones (WorkshopSource)
// beside the player's own mods. Uploading is for the mod's author and is a command of the runtime, never something a script does.
public sealed unsafe partial class SteamSystem
{
    /// <summary>A Workshop item finished installing or updating on the disk (its id). Mods are read when the game starts: tell the player to restart for it to apply.</summary>
    [Signal]
    public Signal<string> WorkshopItemInstalled { get; } = new();

    /// <summary>A download that <see cref="DownloadWorkshopItem"/> started ended: the item id and whether it worked.</summary>
    [Signal]
    public Signal<string, bool> WorkshopItemDownloaded { get; } = new();

    /// <summary>A <see cref="Subscribe"/> finished: the item id and whether it worked.</summary>
    [Signal]
    public Signal<string, bool> WorkshopSubscribed { get; } = new();

    /// <summary>An <see cref="Unsubscribe"/> finished: the item id and whether it worked.</summary>
    [Signal]
    public Signal<string, bool> WorkshopUnsubscribed { get; } = new();

    /// <summary>The ids of the Workshop items the player is subscribed to.</summary>
    [Method]
    public string[] SubscribedItems()
    {
        if (!IsActive)
        {
            return [];
        }

        uint count = SteamApi.UgcGetNumSubscribedItems(_ugc, false);
        ulong[] ids = new ulong[count];
        fixed (ulong* buffer = ids)
        {
            count = SteamApi.UgcGetSubscribedItems(_ugc, buffer, count, false);
        }

        string[] result = new string[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = Id(ids[i]);
        }

        return result;
    }

    /// <summary>Subscribes to a Workshop item (Steam downloads it); <see cref="WorkshopSubscribed"/> follows.</summary>
    [Method]
    public void Subscribe(string itemId)
    {
        if (!IsActive || !TryId(itemId, out ulong id))
        {
            WorkshopSubscribed.Emit(itemId, false);
            return;
        }

        Watch<RemoteStorageSubscribePublishedFileResult>(SteamApi.UgcSubscribeItem(_ugc, id),
            result => WorkshopSubscribed.Emit(itemId, result.Result == EResult.OK),
            () => WorkshopSubscribed.Emit(itemId, false));
    }

    /// <summary>Unsubscribes from a Workshop item (Steam deletes it); <see cref="WorkshopUnsubscribed"/> follows.</summary>
    [Method]
    public void Unsubscribe(string itemId)
    {
        if (!IsActive || !TryId(itemId, out ulong id))
        {
            WorkshopUnsubscribed.Emit(itemId, false);
            return;
        }

        Watch<RemoteStorageUnsubscribePublishedFileResult>(SteamApi.UgcUnsubscribeItem(_ugc, id),
            result => WorkshopUnsubscribed.Emit(itemId, result.Result == EResult.OK),
            () => WorkshopUnsubscribed.Emit(itemId, false));
    }

    /// <summary>Asks Steam to download (or update) an item now, ahead of the others; <see cref="WorkshopItemDownloaded"/> follows. False when Steam refused.</summary>
    [Method]
    public bool DownloadWorkshopItem(string itemId) => IsActive && TryId(itemId, out ulong id) && SteamApi.UgcDownloadItem(_ugc, id, true);

    /// <summary>The folder an installed item is in, or "" when it is not installed (yet).</summary>
    [Method]
    public string WorkshopItemFolder(string itemId) => IsActive && TryId(itemId, out ulong id) ? InstalledFolder(id) ?? "" : "";

    /// <summary>The folders of the subscribed items that are installed, with the item id of each: the mods Steam brought. An item that is still downloading is not listed.</summary>
    public IReadOnlyList<(string ItemId, string Folder)> InstalledWorkshopItems()
    {
        var items = new List<(string, string)>();
        if (!IsActive)
        {
            return items;
        }

        foreach (string item in SubscribedItems())
        {
            if (TryId(item, out ulong id) && InstalledFolder(id) is { } folder)
            {
                items.Add((item, folder));
            }
        }

        return items;
    }

    private string? InstalledFolder(ulong id)
    {
        uint state = SteamApi.UgcGetItemState(_ugc, id);
        if ((state & (uint)EItemState.Installed) == 0)
        {
            return null;
        }

        byte* folder = stackalloc byte[1024];
        ulong size;
        uint stamp;
        return SteamApi.UgcGetItemInstallInfo(_ugc, id, &size, folder, 1024, &stamp) ? Utf8.FromPointer(folder) : null;
    }

    // ---- Uploading --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Publishes a mod folder as a Workshop item, or updates one (<see cref="WorkshopUpload.ItemId"/>). <paramref name="done"/> runs from <see cref="Update"/> with the outcome. This makes
    /// something public under the player's name: only the runtime's upload command calls it, after the author asked for it.
    /// </summary>
    public void UploadWorkshopItem(WorkshopUpload upload, Action<WorkshopUploadResult> done)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(done);
        if (!IsActive)
        {
            done(new WorkshopUploadResult(false, upload.ItemId ?? "", Status, false));
            return;
        }

        if (!Directory.Exists(upload.Folder) || !File.Exists(Path.Combine(upload.Folder, "mod.xml")))
        {
            done(new WorkshopUploadResult(false, upload.ItemId ?? "", $"{upload.Folder} is not a mod folder (it has no mod.xml).", false));
            return;
        }

        uint app = AppIdValue;
        if (TryId(upload.ItemId, out ulong existing))
        {
            SubmitUpload(app, existing, upload, done);
            return;
        }

        Watch<CreateItemResult>(SteamApi.UgcCreateItem(_ugc, app, EWorkshopFileType.Community), created =>
        {
            if (created.Result != EResult.OK)
            {
                done(new WorkshopUploadResult(false, "", $"Steam could not create the item: {created.Result}.", created.UserNeedsToAcceptWorkshopLegalAgreement));
                return;
            }

            Log.Info("Steam", $"Created Workshop item {Id(created.PublishedFileId)}.");
            SubmitUpload(app, created.PublishedFileId, upload, done, created.UserNeedsToAcceptWorkshopLegalAgreement);
        }, () => done(new WorkshopUploadResult(false, "", "Steam could not create the item.", false)));
    }

    private void SubmitUpload(uint app, ulong item, WorkshopUpload upload, Action<WorkshopUploadResult> done, bool needsAgreement = false)
    {
        ulong update = SteamApi.UgcStartItemUpdate(_ugc, app, item);
        string id = Id(item);
        fixed (byte* folder = Utf8.ToZeroTerminated(Path.GetFullPath(upload.Folder)))
        {
            bool ok = SteamApi.UgcSetItemContent(_ugc, update, folder);
            if (upload.Title is not null)
            {
                fixed (byte* title = Utf8.ToZeroTerminated(upload.Title))
                {
                    ok &= SteamApi.UgcSetItemTitle(_ugc, update, title);
                }
            }

            if (upload.Description is not null)
            {
                fixed (byte* text = Utf8.ToZeroTerminated(upload.Description))
                {
                    ok &= SteamApi.UgcSetItemDescription(_ugc, update, text);
                }
            }

            if (upload.Preview is not null)
            {
                fixed (byte* preview = Utf8.ToZeroTerminated(Path.GetFullPath(upload.Preview)))
                {
                    ok &= SteamApi.UgcSetItemPreview(_ugc, update, preview);
                }
            }

            ok &= SteamApi.UgcSetItemVisibility(_ugc, update, upload.Visibility switch
            {
                WorkshopVisibility.Public => ERemoteStoragePublishedFileVisibility.Public,
                WorkshopVisibility.FriendsOnly => ERemoteStoragePublishedFileVisibility.FriendsOnly,
                WorkshopVisibility.Unlisted => ERemoteStoragePublishedFileVisibility.Unlisted,
                _ => ERemoteStoragePublishedFileVisibility.Private,
            });
            if (upload.Tags.Count > 0)
            {
                ok &= SetTags(update, upload.Tags);
            }

            if (!ok)
            {
                done(new WorkshopUploadResult(false, id, "Steam refused part of the item's description (check the title, the preview file and the content folder).", needsAgreement));
                return;
            }
        }

        fixed (byte* note = Utf8.ToZeroTerminated(upload.ChangeNote ?? ""))
        {
            Watch<SubmitItemUpdateResult>(SteamApi.UgcSubmitItemUpdate(_ugc, update, note), submitted =>
            {
                bool agreement = needsAgreement || submitted.UserNeedsToAcceptWorkshopLegalAgreement;
                done(new WorkshopUploadResult(submitted.Result == EResult.OK, id, submitted.Result == EResult.OK ? "Uploaded." : $"Steam refused the upload: {submitted.Result}.", agreement));
            }, () => done(new WorkshopUploadResult(false, id, "The upload failed.", needsAgreement)));
        }

        _uploading = update;
    }

    private ulong _uploading;

    /// <summary>The upload in progress: its stage, and the bytes sent of the bytes to send. Stage is "" when nothing is uploading.</summary>
    public (string Stage, ulong Sent, ulong Total) UploadProgress()
    {
        if (!IsActive || _uploading == 0)
        {
            return ("", 0, 0);
        }

        ulong sent, total;
        EItemUpdateStatus status = SteamApi.UgcGetItemUpdateProgress(_ugc, _uploading, &sent, &total);
        return (status == EItemUpdateStatus.Invalid ? "" : status.ToString(), sent, total);
    }

    private bool SetTags(ulong update, IReadOnlyList<string> tags)
    {
        byte[][] encoded = new byte[tags.Count][];
        for (int i = 0; i < encoded.Length; i++)
        {
            encoded[i] = Utf8.ToZeroTerminated(tags[i]);
        }

        // Pin every tag, then hand Steam the array of pointers.
        var handles = new System.Runtime.InteropServices.GCHandle[encoded.Length];
        byte*[] pointers = new byte*[encoded.Length];
        try
        {
            for (int i = 0; i < encoded.Length; i++)
            {
                handles[i] = System.Runtime.InteropServices.GCHandle.Alloc(encoded[i], System.Runtime.InteropServices.GCHandleType.Pinned);
                pointers[i] = (byte*)handles[i].AddrOfPinnedObject();
            }

            fixed (byte** strings = pointers)
            {
                var array = new SteamParamStringArray { Strings = strings, NumStrings = pointers.Length };
                return SteamApi.UgcSetItemTags(_ugc, update, &array, false);
            }
        }
        finally
        {
            foreach (System.Runtime.InteropServices.GCHandle handle in handles)
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
        }
    }
}

/// <summary>Who can see a Workshop item.</summary>
public enum WorkshopVisibility
{
    /// <summary>Only the author (the safe default for a first upload).</summary>
    Private,

    FriendsOnly,

    /// <summary>Anyone with the link, but not listed.</summary>
    Unlisted,

    Public,
}

/// <summary>What <see cref="SteamSystem.UploadWorkshopItem"/> sends.</summary>
public sealed record WorkshopUpload
{
    /// <summary>The mod folder: it has a <c>mod.xml</c>, and everything in it is uploaded.</summary>
    public required string Folder { get; init; }

    /// <summary>The item to update, or null to create a new item.</summary>
    public string? ItemId { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    /// <summary>An image file (JPEG, PNG or GIF, under 1 MB) shown on the item's page.</summary>
    public string? Preview { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public WorkshopVisibility Visibility { get; init; } = WorkshopVisibility.Private;

    /// <summary>Shown in the item's change log.</summary>
    public string? ChangeNote { get; init; }
}

/// <summary>How an upload ended. Steam wants the author to accept the Workshop agreement once, in a browser, before the item can be public: then <see cref="NeedsLegalAgreement"/> is true.</summary>
public readonly record struct WorkshopUploadResult(bool Ok, string ItemId, string Message, bool NeedsLegalAgreement);
