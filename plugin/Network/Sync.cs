using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace MassiveCasualties.Network;

/// <summary>
///     Syncs data for every ISyncItemOrBuilding.
///     Note: this only works if they are buildings AND dynamic objects (rigidbody with dynamic).
///     This works with CUCoreLib registered buildings/items because those are injected into the standard
///     sync flow, and are instantiated before this packet is decoded.
/// </summary>
internal static class ObjectSync
{
    private static readonly ConditionalWeakTable<GameObject, ISyncItemOrBuilding[]> ObjectToSyncs = new();

    internal static void UpdatePacket(ref ItemOrBuildingCoolDeltaCompressablePacket packet, in SyncInfo syncInfo)
    {
        foreach (var item in GetComponents(syncInfo.go))
        {
            item.UpdatePacket(ref packet, syncInfo);
        }
    }

    internal static void ApplyPacket(in ItemOrBuildingCoolDeltaCompressablePacket packet, SyncInfo syncInfo)
    {
        foreach (var item in GetComponents(syncInfo.go))
        {
            item.ApplyPacket(packet, syncInfo);
        }
    }

    private static ISyncItemOrBuilding[] GetComponents(GameObject go)
    {
        ISyncItemOrBuilding[] items;
        if (!ObjectToSyncs.TryGetValue(go, out items))
        {
            items = go.GetComponents<ISyncItemOrBuilding>();
            ObjectToSyncs.Add(go, items);
        }

        return items;
    }
}

/// <summary>
///     Used by a custom item/building to hijack the existing sync
///     infrastructure for its own data.
/// </summary>
internal interface ISyncItemOrBuilding
{
    /// <summary>
    ///     Called before sending a packet out so the object can
    ///     update the packet with any relevant details.
    /// </summary>
    void UpdatePacket(ref ItemOrBuildingCoolDeltaCompressablePacket packet, in SyncInfo syncInfo);

    /// <summary>
    ///     Called when a packet is received on the client so the object
    ///     can be updated with the new info.
    /// </summary>
    void ApplyPacket(in ItemOrBuildingCoolDeltaCompressablePacket packet, SyncInfo syncInfo);
}

[StructLayout(LayoutKind.Explicit)]
internal struct ULongToShorts
{
    [FieldOffset(0)] public ulong Value;

    [FieldOffset(0)] public short Short0;
    [FieldOffset(2)] public short Short1;
    [FieldOffset(4)] public short Short2;
    [FieldOffset(6)] public short Short3;
}