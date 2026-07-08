using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using Unity.VisualScripting;
using UnityEngine;

namespace MassiveCasualties.Behaviors;

/// <summary>
///     Does a periodic full sync of all entities to every player, also making
///     sure they're network registered.
///     This is needed for saves (which use network objects to save entities)
///     and host switching (without this, the new host would only know about entities
///     immediately surrounding them).
/// </summary>
internal class EntitySync : MonoBehaviour
{
    private Coroutine _slowSyncCoroutine;

    private bool DisallowSync => !Net.is_server || !WorldGeneration.world || WorldGeneration.world.generatingWorld;

    private void Awake()
    {
        _slowSyncCoroutine = StartCoroutine(SlowSyncLoop());
    }

    private void OnDestroy()
    {
        if (_slowSyncCoroutine != null)
        {
            StopCoroutine(_slowSyncCoroutine);
            _slowSyncCoroutine = null;
        }
    }

    private IEnumerator SlowSyncLoop()
    {
        while (true)
        {
            while (DisallowSync)
            {
                yield return new WaitForSeconds(1f);
            }

            yield return SlowSync();

            // This might mean we're changing servers, in which case
            // we want to do another sync as soon as possible.
            if (DisallowSync) continue;

            // Delay between syncs.
            // If another player joins, we should immediately resync,
            // since that player will need to have all the entities to be
            // able to be the new host.
            var lastPlayers = Enumerable.ToHashSet(NetPlayer.ClientIdToPlayerDict.Keys);
            for (var i = 0; i < 10; i++)
            {
                var newPlayers = Enumerable.ToHashSet(NetPlayer.ClientIdToPlayerDict.Keys);
                if (DisallowSync || !lastPlayers.SetEquals(newPlayers)) break;

                yield return new WaitForSeconds(1f);
            }
        }
    }

    /// <summary>
    ///     Synchronizes every game object with every player, regardless of distance.
    ///     Based on NewCoolerObjectPacketWriteReadSystem.Server_RunFastSync.
    ///     Must only run on servers.
    /// </summary>
    private IEnumerator SlowSync()
    {
        var sync = NewCoolerObjectPacketWriteReadSystem.inst;
        if (sync == null) yield break;

        var buildings = GetAllSyncObjects();
        foreach (var building in buildings)
        {
            // This is always needed (even if there are no players),
            // since save/load depends on net objects.
            NetObjectRegistry.NewGO(building);
        }

        var totalIterations = 0;

        // Making the outer loop per-object (instead of per-player) allows some level
        // of concurrency, as all the queues fill up at once.
        // It isn't as good as doing them all separately, though.
        foreach (var obj in buildings)
        {
            foreach (var entry in NetPlayer.ClientIdToPlayerDict)
            {
                var id = entry.Key;
                var plr = entry.Value;

                // Make sure we're not blocking anything.
                // Note: a full sync is ~2000 objects * # of players.
                totalIterations++;
                if (totalIterations % 500 == 0)
                {
                    yield return null;
                }
                else if (totalIterations % 2000 == 0) yield return new WaitForSeconds(0.1f);

                if (DisallowSync) yield break;

                // Dictionary lookup is somewhat expensive to do for every object, but pretty
                // small in the grand scheme of things.
                if (plr == null || !sync.server_perplrstates.TryGetValue(id, out var plrstate)) continue;

                // If the player is overloaded, we should back off to avoid clogging the network.
                while (plrstate.forcesync_queue.Count >= (plr.IsAlive() ? 100 : 20))
                {
                    yield return new WaitForSeconds(0.1f);

                    if (DisallowSync) yield break;
                }

                if (NetObjectRegistry.TryGetSyncInfoOrRegister(obj, out var si))
                {
                    // Can't call sync.Server_Internal_QueueForceSync because it does
                    // a distance check.
                    if (!plrstate.forcesync_queue.Contains(si.syncId))
                    {
                        plrstate.forcesync_queue.Enqueue(si.syncId);
                    }

                    sync.server_has_queued_forcesync = true;
                }
            }
        }
    }

    /// <summary>
    ///     Gets every object in the level that should be synced.
    ///     Based on NetObjectRegistry.GatherClosestObjectsToSync.
    /// </summary>
    private static HashSet<GameObject> GetAllSyncObjects()
    {
        // Includes backgroundified buildings (which are disabled).
        var objs = Enumerable.ToHashSet(FindObjectsOfType<Item>(true).Select(x => x.gameObject)
            .Concat(FindObjectsOfType<BuildingEntity>(true).Select(x => x.gameObject)));

        // Add inner items.
        foreach (var gameObject in objs)
        {
            if (gameObject.TryGetComponent(out Container component))
            {
                objs.AddRange(component.GetAllItems()
                    .Select(x => x.gameObject));
            }
        }

        // Add player items.
        foreach (var netBody in NetBody.all_instances)
        {
            objs.AddRange(netBody.body.GetAllItemsThorough().Select(x => x.gameObject));
        }

        objs.RemoveWhere(x => x == null || NetObjectRegistry.ObjectCanBeIgnoredForNetwork(x));
        return objs;
    }
}