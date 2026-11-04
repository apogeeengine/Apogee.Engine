namespace Apogee.Scripting;

/// <summary>
/// Whose code is running: the base game or one mod. Everything an owner's scripts allocate is counted in the owner's memory
/// category of the VM, and its calls get the owner's time budget, so one mod cannot use up the memory or the frame of the others.
/// </summary>
public sealed class ScriptOwner
{
    internal ScriptOwner(LuauVm vm, int index, string id, bool isMod, int category, long memoryLimit, int timeBudgetMilliseconds)
    {
        Vm = vm;
        Index = index;
        Id = id;
        IsMod = isMod;
        Category = category;
        MemoryLimit = memoryLimit;
        TimeBudgetMilliseconds = timeBudgetMilliseconds;
    }

    internal LuauVm Vm { get; }

    /// <summary>Position in the VM's list; the thread data of the owner's threads is this plus one.</summary>
    internal int Index { get; }

    public string Id { get; }

    /// <summary>True for a mod. Mods cannot use classes marked <c>ModsBlocked</c>, and (with packages, later) are confined to their own files.</summary>
    public bool IsMod { get; }

    /// <summary>The memory category (1 to 255) of this owner's objects; the base game's is 0.</summary>
    public int Category { get; }

    /// <summary>A soft limit in bytes (0: none): going over it is a memory error in this owner's code, at its next call or loop iteration.</summary>
    public long MemoryLimit { get; }

    /// <summary>The time one call of this owner's code may take, in milliseconds (0: the VM's default).</summary>
    public int TimeBudgetMilliseconds { get; }

    /// <summary>Bytes the owner's objects take now.</summary>
    public long MemoryUsed => Vm.MemoryUsed(this);

    public override string ToString() => Id;
}
