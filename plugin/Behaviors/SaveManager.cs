// A significant portion of this file is derived from the
// game's source code, and as such IS NOT provided under
// any license that the rest of the project is distributed under.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib.Utils;
using MassiveCasualties.Patches;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Steamworks;
using Unity.VisualScripting;
using UnityEngine;
using CoolSyncSubSystemForObjects = KrokoshaCasualtiesMP.CoolSyncSubSystemForObjects;
using knetid = KrokoshaCasualtiesMP.knetid;
using KSteam = KrokoshaCasualtiesMP.KSteam;
using NetBody = KrokoshaCasualtiesMP.NetBody;
using NetPlayer = KrokoshaCasualtiesMP.NetPlayer;
using NewCoolerObjectPacketWriteReadSystem = KrokoshaCasualtiesMP.NewCoolerObjectPacketWriteReadSystem;
using Random = UnityEngine.Random;
using SavesystemPatch = KrokoshaCasualtiesMP.SavesystemPatch;
using WorldGeneration_GenerateWorld_MultiplayerPatch =
    KrokoshaCasualtiesMP.WorldGeneration_GenerateWorld_MultiplayerPatch;

namespace MassiveCasualties.Behaviors;

[HarmonyPatch]
internal class SaveManager : MonoBehaviour
{
    /// <summary>
    ///     From the last session that the player was in, before
    ///     they moved to a new session.
    ///     This can be when they're either a host or a client.
    /// </summary>
    internal static SaveInfo LastSessionSave { get; private set; }

    internal static CSteamID LastSessionSaveLobby { get; private set; }

    internal static WorldSave LastWorldSave { get; private set; }

    internal static CSteamID LastWorldSaveLobby { get; private set; }

    internal static void SaveBeforeSessionChange()
    {
        SavePlayer();

        // World gets saved if we're the host, that way we can restore it.
        // TODO: Should we still save if there are other players?
        if (HardcodedServer.CurHostID == NetPlayer.LOCAL_PLAYER.clientId) SaveWorld();
    }

    private static void SavePlayer()
    {
        InternalSavePatched();
        LastSessionSaveLobby = KSteam.CURRENT_LOBBY.lobby_steamID;

        Plugin.Logger.LogInfo("Saved session - " + LastSessionSave.cId);
    }

    private static void SaveWorld()
    {
        // TODO: Support multiple saves (store lobby ID) and dead players.
        LastWorldSave = new WorldSave(NetPlayer.LOCAL_PLAYER.playerbody.position);
        LastWorldSaveLobby = KSteam.CURRENT_LOBBY.lobby_steamID;

        Plugin.Logger.LogInfo("Saved world - " + LastWorldSaveLobby);
    }

    /// <summary>
    ///     Saves the game into _lastSessionSave.
    /// </summary>
    [HarmonyReversePatch]
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.SaveGame))]
    private static void InternalSavePatched()
    {
        IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var newJsonSerializerSettings = typeof(JsonSerializerSettings).GetConstructor([]);

            // Just need to remove the json serialize and store it in _lastSessionSave.
            var matcher = new CodeMatcher(instructions)
                .MatchForward(false, new CodeMatch(OpCodes.Newobj, newJsonSerializerSettings))
                .ThrowIfInvalid("Couldn't find new JsonSerializerSettings!")
                .MatchBack(false, new CodeMatch(OpCodes.Ldloc_S))
                .ThrowIfInvalid("Couldn't find load for saveState!");

            // The rest of the code needs to be valid, so it's
            // easier to preserve the original ldloc.s.
            matcher.InsertAndAdvance(matcher.Instruction.Clone());
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Call,
                SymbolExtensions.GetMethodInfo(() => PlayerSaveCallback(null))));
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ret));

            return matcher
                .Instructions();
        }

        _ = Transpiler(null);
    }

    private static void PlayerSaveCallback(SaveInfo saveInfo)
    {
        LastSessionSave = saveInfo;

        var json = JObject.Parse(JsonConvert.SerializeObject(saveInfo, Formatting.None, new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        }));

        LoadSaveForPlayer(NetPlayer.LOCAL_PLAYER.playerbody, json);
    }

    /// <summary>
    ///     Serializes a SaveInfo into a JSON string.
    ///     May throw an error.
    /// </summary>
    internal static string SerializePlayerSave(SaveInfo saveInfo)
    {
        return JsonConvert.SerializeObject(saveInfo, Formatting.None, new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        });
    }

    /// <summary>
    ///     Tries to load the given jobject (serialized SaveInfo) into the body.
    ///     This is a modified version of SaveSystem.TryLoad, with some extra strictness
    ///     to make it safer for arbitrary input.
    /// </summary>
    internal static bool LoadSaveForPlayer(NetBody ply, JObject jobject)
    {
        if (ply == null || jobject == null) return false;

        try
        {
            // Their items must be cleared (mostly, emergency flashlight),
            // otherwise inventory loading will cause errors.
            for (var slot = 0; slot < ply.body.slots.Length; slot++)
            {
                var item = ply.body.GetItem(slot);
                if (item != null)
                {
                    DestroyImmediate(item.gameObject);
                }
            }

            DeserializeBody(ply, jobject);

            DeserializeLimbs(ply, jobject);

            if (!DeserializeItems(ply, jobject)) return false;

            DeserializeBodyComponents(ply, jobject);

            DeserializeLimbComponents(ply, jobject);

            DeserializePlayerDetails(ply, jobject);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("Error applying user save state: " + e);
            return false;
        }

        return true;
    }

    private static void DeserializeBody(NetBody ply, JObject jobject)
    {
        foreach (var bodyField in jobject["body"].Children<JProperty>())
        {
            // TODO: Need to add a field-level whitelist.
            var fieldUserProvided = typeof(Body).GetField(bodyField.Name);
            if (fieldUserProvided == null)
            {
                Plugin.Logger.LogError(
                    $"Trying to load body field \"{bodyField.Name}\", but such field does not exist. Loading will continue.");
            }
            else if (!Attribute.IsDefined(fieldUserProvided, typeof(JsonPropertyAttribute)))
            {
                Plugin.Logger.LogError(
                    $"Trying to load body field \"{bodyField.Name}\", but it is not a JSON field. Loading will continue.");
            }
            else
            {
                try
                {
                    var fieldType = fieldUserProvided.FieldType;
                    var obj = fieldType == typeof(Skills)
                        ? SafeToObject(bodyField.Value, typeof(Skills))
                        : SafeChangeType(bodyField.Value, fieldType);
                    fieldUserProvided.SetValue(ply.body, obj);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(
                        $"Error occured during setting body field \"{fieldUserProvided.Name}\".\n{ex.Message}\n{ex.StackTrace}\nLoading will continue.");
                }
            }
        }
    }

    private static void DeserializeLimbs(NetBody ply, JObject jobject)
    {
        var limbs = jobject["limbs"].Children<JObject>().ToArray();
        for (var i = 0; i < limbs.Length; ++i)
        {
            foreach (var limbField in limbs[i].Children<JProperty>())
            {
                // TODO: Need to add a field-level whitelist.
                var fieldUserProvided = typeof(Limb).GetField(limbField.Name);
                if (fieldUserProvided == null)
                {
                    Plugin.Logger.LogError(
                        $"Trying to load limb field \"{limbField.Name}\" on limb \"{ply.body.limbs[i]}\", but such field does not exist. Loading will continue.");
                }
                else if (!Attribute.IsDefined(fieldUserProvided, typeof(JsonPropertyAttribute)))
                {
                    Plugin.Logger.LogError(
                        $"Trying to load limb field \"{limbField.Name}\" on limb \"{ply.body.limbs[i]}\", but it is not a JSON field. Loading will continue.");
                }
                else
                {
                    try
                    {
                        var fieldType = fieldUserProvided.FieldType;
                        var obj = SafeChangeType(limbField.Value, fieldType);
                        fieldUserProvided.SetValue(ply.body.limbs[i], obj);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogError(
                            $"Error occured during setting \"{ply.body.limbs[i]}\" field \"{fieldUserProvided.Name}\".\n{ex.Message}\n{ex.StackTrace}\nLoading will continue.");
                    }
                }
            }
        }
    }

    private static bool DeserializeItems(NetBody ply, JObject jobject)
    {
        SavedItem[] savedItemArray;
        try
        {
            savedItemArray = SafeToObject(jobject["items"], typeof(SavedItem[])) as SavedItem[];
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Error occured during parsing global item data.\n{ex.Message}\n{ex.StackTrace}\nLoading has been cancelled.");
            return false;
        }

        // TODO: Find a better maximum length.
        if (savedItemArray.Length >= 255)
        {
            Plugin.Logger.LogError("savedItemArray has too many items!");
            return false;
        }

        for (var i = 0; i < savedItemArray.Length; i++)
        {
            var savedItem = savedItemArray[i];
            GameObject gameObject;
            Item item;
            try
            {
                if (!Item.GlobalItems.ContainsKey(savedItem.id))
                {
                    Plugin.Logger.LogError($"Item \"{savedItem.id}\" does not exist! Loading will continue.");
                    continue;
                }

                gameObject = Instantiate(Resources.Load(savedItem.id), ply.body.transform.position,
                    Quaternion.identity) as GameObject;
                item = gameObject.GetComponent<Item>();
                if (item == null)
                {
                    Plugin.Logger.LogError(
                        $"Item \"{savedItem.id}\" does not have an Item component! Loading will continue.");
                    continue;
                }

                item.condition = savedItem.condition;
                item.favourited = savedItem.favourited;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    $"Error occured during creating item \"{savedItem.id}\".\n{ex.Message}\n{ex.StackTrace}\nLoading will continue.");
                continue;
            }

            try
            {
                if (savedItem.slot >= 0)
                {
                    if (ply.body.HoldingItem(savedItem.slot))
                    {
                        ply.body.GetItem(savedItem.slot).GetComponent<Container>().LoadItem(item);
                    }
                    else
                    {
                        ply.body.PickUpItem(item, savedItem.slot, true);
                    }
                }
                else if (ply.body.GetWearableBySlotID(savedItem.wearSlot))
                {
                    ply.body.GetWearableBySlotID(savedItem.wearSlot).GetComponent<Container>()
                        .LoadItem(item);
                }
                else
                {
                    ply.body.WearWearable(item);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    $"Error occured during picking up item \"{item}\".\n{ex.Message}\n{ex.StackTrace}\nLoading will continue.");
                continue;
            }

            var itemComponents = jobject["itemComponents"][i];
            if (itemComponents == null) continue;

            foreach (var itemComponent in itemComponents.Children<JProperty>())
            {
                // This arbitrary type lookup is constrained by only allowing
                // types that exist on the resource.
                // TODO: Need to add a field-level whitelist.
                var typeUserProvided = SafeGetComponentType(itemComponent.Name);
                if (typeUserProvided == null)
                {
                    Plugin.Logger.LogError(
                        $"Error occured during loading item \"{item}\". \"{itemComponent.Name}\" is not a valid type. Loading will continue.");
                }
                else
                {
                    var component = gameObject.GetComponent(typeUserProvided);
                    if (component == null)
                    {
                        Plugin.Logger.LogError(
                            $"Error occured during loading item \"{item}\". Component for \"{typeUserProvided.Name}\" doesn't exist on object. Loading will continue.");
                    }
                    else
                    {
                        foreach (var componentField in itemComponent.Value.Children<JProperty>())
                        {
                            var fieldUserProvided = typeUserProvided.GetField(componentField.Name);
                            if (fieldUserProvided == null)
                            {
                                Plugin.Logger.LogError(
                                    $"Error occured during loading item \"{item}\". Field for \"{componentField.Name}\" doesn't exist. Loading will continue.");
                            }
                            else if (!Attribute.IsDefined(fieldUserProvided, typeof(JsonPropertyAttribute)))
                            {
                                Plugin.Logger.LogError(
                                    $"Error occured during loading item \"{item}\". Field \"{componentField.Name}\" is not a JSON field! Loading will continue.");
                            }
                            else
                            {
                                try
                                {
                                    var fieldType = fieldUserProvided.FieldType;
                                    var obj = !(fieldType == typeof(List<LiquidStack>))
                                        ? SafeToObject(componentField.Value, fieldType)
                                        : SafeToObject(componentField.Value, typeof(List<LiquidStack>));
                                    fieldUserProvided.SetValue(component, obj);
                                }
                                catch (Exception ex)
                                {
                                    Plugin.Logger.LogError(
                                        $"Error occured during loading item \"{item}\".\n{ex.Message}\n{ex.StackTrace}\nLoading will continue.");
                                }
                            }
                        }
                    }
                }
            }
        }

        return true;
    }

    private static void DeserializeBodyComponents(NetBody ply, JObject jobject)
    {
        try
        {
            foreach (var bodyComponent in jobject["bodyComponents"].Children<JProperty>())
            {
                // The original used AddComponent, which is never safe.
                // TODO: Need to add a component and field-level whitelist.
                var type = SafeGetComponentType(bodyComponent.Name);
                var component = ply.body.gameObject.GetComponent(type);
                if (component == null)
                {
                    Plugin.Logger.LogError(
                        $"Error occured during loading body components. Component \"{bodyComponent.Name}\" does not exist! Loading will continue.");
                }
                else
                {
                    foreach (var bodyField in bodyComponent.Value.Children<JProperty>())
                    {
                        var fieldUserProvided = type.GetField(bodyField.Name);
                        if (fieldUserProvided == null)
                        {
                            Plugin.Logger.LogError(
                                $"Error occured during loading body components, component \"{bodyComponent.Name}\". Field \"${bodyField.Name}\" does not exist! Loading will continue.");
                        }
                        else if (!Attribute.IsDefined(fieldUserProvided, typeof(JsonPropertyAttribute)))
                        {
                            Plugin.Logger.LogError(
                                $"Error occured during loading body components, component \"{bodyComponent.Name}\". Field \"${bodyField.Name}\" is not a JSON field! Loading will continue.");
                        }
                        else
                        {
                            var fieldType = fieldUserProvided.FieldType;
                            var obj = SafeChangeType(bodyField.Value, fieldType);
                            fieldUserProvided.SetValue(component, obj);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Error occured during applying player body components.\n{ex.Message}\n{ex.StackTrace}\nLoading will continue.");
        }
    }

    private static void DeserializeLimbComponents(NetBody ply, JObject jobject)
    {
        try
        {
            for (var i = 0; i < ply.body.limbs.Length; ++i)
            {
                var jLimbComponent = jobject["limbComponents"][i];
                if (jLimbComponent == null) continue;

                foreach (var limbComponent in jLimbComponent.Children<JProperty>())
                {
                    // Same as above, rewritten to use GetComponent.
                    // TODO: Need to add a component and field-level whitelist.
                    var type = SafeGetComponentType(limbComponent.Name);
                    var component = ply.body.limbs[i].gameObject.GetComponent(type);
                    if (component == null)
                    {
                        Plugin.Logger.LogError(
                            $"Error occured during loading limb components. Component \"{limbComponent.Name}\" does not exist! Loading will continue.");
                    }
                    else
                    {
                        foreach (var limbField in limbComponent.Value.Children<JProperty>())
                        {
                            var fieldUserProvided = type.GetField(limbField.Name);
                            if (fieldUserProvided == null)
                            {
                                Plugin.Logger.LogError(
                                    $"Error occured during loading limb components, component \"{limbComponent.Name}\". Field \"${limbField.Name}\" does not exist! Loading will continue.");
                            }
                            else if (!Attribute.IsDefined(fieldUserProvided, typeof(JsonPropertyAttribute)))
                            {
                                Plugin.Logger.LogError(
                                    $"Error occured during loading limb components, component \"{limbComponent.Name}\". Field \"${limbField.Name}\" is not a JSON field! Loading will continue.");
                            }
                            else
                            {
                                var fieldType = fieldUserProvided.FieldType;
                                var obj = SafeChangeType(limbField.Value, fieldType);
                                fieldUserProvided.SetValue(component, obj);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Error occured during applying player body components.\n{ex.Message}\n{ex.StackTrace}\nLoading will continue.");
        }
    }

    private static void DeserializePlayerDetails(NetBody ply, JObject jobject)
    {
        try
        {
            ply.body.lastHappiness = jobject["lastHappiness"].ToArray()
                .Select(jv => (float)jv).ToArray();
            // TODO: Can we use this?
            /*WoundView.view.SetCharDetails((int)jobject["cHeight"], (int)jobject["cAge"], (int)jobject["cId"],
                    (int)jobject["cVer"]);
                SavedRecipeData savedRecipeData = SafeToObject(jobject["savedRecipeData"], typeof(SavedRecipeData));
                for (var index = 0; index < savedRecipeData.saved.Length; ++index)
                {
                    Recipes.recipes[index].hasMadeBefore = savedRecipeData.saved[index].Item1;
                    Recipes.recipes[index].INT = savedRecipeData.saved[index].Item2;
                }*/
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(
                $"Error occured during applying player details.\n{ex.Message}\n{ex.StackTrace}\nLoading will continue.");
        }
    }

    /// <summary>
    ///     Only returns types of components from Assembly-CSharp.
    /// </summary>
    private static Type SafeGetComponentType(string typeName)
    {
        if (typeName.Contains(" ")) return null;

        var type = typeof(SaveSystem).Assembly.GetType(typeName);
        if (type != null && typeof(MonoBehaviour).IsAssignableFrom(type)) return type;

        return null;
    }

    /// <summary>
    ///     Deserializes according to a whitelist, throwing an error if
    ///     it doesn't match.
    /// </summary>
    private static object SafeToObject(JToken token, Type type)
    {
        if (!TypeWhitelist.IsTypeAllowed(type))
        {
            throw new Exception($"Cannot deserialize type {type.FullName} (not allowed).");
        }

        var secureSerializer = new JsonSerializer
        {
            // Needed to prevent Newtonsoft's default behavior
            // of allowing objects to be deserialized to arbitrary types.
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 32,
            SerializationBinder = new NullBinder(),
            ContractResolver = new TypeWhitelist()
        };

        return token.ToObject(type, secureSerializer);
    }

    /// <summary>
    ///     Calls Convert.ChangeType with a whitelist, throwing an error if
    ///     it doesn't match.
    /// </summary>
    private static object SafeChangeType(object value, Type type)
    {
        if (!TypeWhitelist.IsTypeAllowed(type))
        {
            throw new Exception($"Cannot convert type {type.FullName} (not allowed).");
        }

        return Convert.ChangeType(value, type);
    }
}

internal class NullBinder : ISerializationBinder
{
    public Type BindToType(string assemblyName, string typeName)
    {
        // Disallow dynamic type lookup.
        return null!;
    }

    public void BindToName(Type serializedType, out string assemblyName, out string typeName)
    {
        assemblyName = null;
        typeName = null;
    }
}

internal class TypeWhitelist : DefaultContractResolver
{
    /// <summary>
    ///     Types that we're allowed to deserialize to.
    ///     Lists and arrays are supported automatically.
    /// </summary>
    private static readonly HashSet<Type> AllowedTypes = new()
    {
        typeof(int), typeof(float), typeof(double), typeof(string), typeof(bool),
        typeof(Vector3), typeof(Quaternion),
        typeof(Skills), typeof(LiquidStack), typeof(SavedItem)
    };

    internal static bool IsTypeAllowed(Type type)
    {
        if (type == null) return false;

        if (type.IsArray) return IsTypeAllowed(type.GetElementType());
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            return IsTypeAllowed(type.GetGenericArguments()[0]);
        }

        return AllowedTypes.Contains(type);
    }

    protected override JsonObjectContract CreateObjectContract(Type objectType)
    {
        if (!IsTypeAllowed(objectType))
        {
            throw new Exception($"Refusing to deserialize object of type ${objectType.FullName}.");
        }

        return base.CreateObjectContract(objectType);
    }
}

internal class WorldSave
{
    /// <summary>
    ///     The save we're currently loading, or null if no
    ///     save is being loaded.
    /// </summary>
    private static WorldSave _loadingSave;

    private static bool _placedTiles;
    private static bool _placedEntities;

    private readonly int _biomeDepth;
    private readonly byte[] _blocks;
    private readonly byte[] _fluids;
    private readonly byte[] _objects;
    private readonly int _randS0;
    private readonly int _randS1;
    private readonly int _randS2;
    private readonly int _randS3;
    private readonly float _returnX;
    private readonly float _returnY;
    private readonly int _totalTraveled;

    /// <summary>
    ///     Turns the current world into a save.
    ///     This MUST be called on the server.
    /// </summary>
    /// <param name="returnPos">The position to return the player to when the save is loaded.</param>
    internal WorldSave(Vector2 returnPos)
    {
        _returnX = returnPos.x;
        _returnY = returnPos.y;
        _biomeDepth = WorldGeneration.world.biomeDepth;
        _totalTraveled = WorldGeneration.world.totalTraveled;

        var rand = WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.randomstate;
        _randS0 = rand.s0;
        _randS1 = rand.s1;
        _randS2 = rand.s2;
        _randS3 = rand.s3;

        _blocks = SavesystemPatch.SerializeWorldBlocks();
        _fluids = SavesystemPatch.SerializeWorldFluids();
        _objects = SerializeNetObjects();
    }

    /// <summary>
    ///     Marks the save for loading on the next world generation.
    /// </summary>
    internal void Load()
    {
        _loadingSave = this;

        _placedTiles = false;
        _placedEntities = false;
    }

    /// <summary>
    ///     Applies any pending rules from this save to state,
    ///     which must be a reference to WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.
    /// </summary>
    internal static void ApplyWorldgenRules(ref LastBeforeGenerationState state)
    {
        if (_loadingSave == null) return;

        state.randomstate = new Random.State
        {
            s0 = _loadingSave._randS0,
            s1 = _loadingSave._randS1,
            s2 = _loadingSave._randS2,
            s3 = _loadingSave._randS3
        };
        state.biomeDepth = (byte)_loadingSave._biomeDepth;
        state.totalTraveled = _loadingSave._totalTraveled;

        Random.state = state.randomstate;
    }

    /// <summary>
    ///     Whether worldgen should be replaced by this system.
    /// </summary>
    internal static bool CustomWorldgen()
    {
        return _loadingSave != null;
    }

    /// <summary>
    ///     If a save is loading, places block/fluid tiles into the world.
    /// </summary>
    internal static void PlaceTilesIdempotent()
    {
        if (_loadingSave == null || _placedTiles) return;

        try
        {
            SavesystemPatch.DeserializeWorldBlocks(_loadingSave._blocks);
            SavesystemPatch.DeserializeWorldFluids(_loadingSave._fluids);
        }
        catch (Exception e)
        {
            // This gets swallowed by the coroutine.
            Plugin.Logger.LogError(e);
            throw e;
        }

        _placedTiles = true;
    }

    /// <summary>
    ///     If a save is loading, places entities into the world.
    ///     Returns whether the world was updated.
    /// </summary>
    internal static void PlaceEntitiesIdempotent()
    {
        if (_loadingSave == null || _placedEntities) return;

        try
        {
            DeserializeNetObjects(_loadingSave._objects);
        }
        catch (Exception e)
        {
            // This gets swallowed by the coroutine.
            Plugin.Logger.LogError(e);
            throw e;
        }

        _placedEntities = true;
    }

    internal static void PlacePlayer(NetBody plr)
    {
        if (_loadingSave == null) return;

        plr.transform.position = new Vector2(_loadingSave._returnX, _loadingSave._returnY);
    }

    /// <summary>
    ///     Call when a save is finished loading.
    /// </summary>
    internal static void FinishLoad()
    {
        _loadingSave = null;
        _placedTiles = false;
        _placedEntities = false;
    }

    /// <summary>
    ///     Serializes all server-side net objects to a byte stream.
    ///     MUST run on the host.
    /// </summary>
    private static byte[] SerializeNetObjects()
    {
        var writer = new NetDataWriter();
        var sync = NewCoolerObjectPacketWriteReadSystem.inst;

        var emptyPlayerState = new CoolSyncSubSystemForObjects.Server_PerPlrState
        {
            system = sync
        };
        var numObjs = 0;

        // Filled with numObjs below.
        writer.Put(0);

        foreach (var obj in sync.server_objects_list)
        {
            if (obj.real_obj == null) continue;

            if (sync.PackObjectForPlr(writer, emptyPlayerState, obj))
            {
                numObjs++;
            }
        }

        var position = writer.SetPosition(0);
        writer.Put(numObjs);
        writer.SetPosition(position);

        return writer.Data;
    }

    /// <summary>
    ///     Serializes net objects saved with SerializeNetObjects.
    ///     This MUST only run on the host, and only before loading
    ///     the instance.
    ///     Based on CoolSyncSubSystemForObjects.Client_Receive
    /// </summary>
    private static void DeserializeNetObjects(byte[] data)
    {
        var reader = new NetDataReader(data);
        var sync = NewCoolerObjectPacketWriteReadSystem.inst;

        reader.Get(out int numObjs);
        for (var i = 0; i < numObjs; i++)
        {
            reader.Get(out knetid netID);
            reader.Get(out byte bitsetSize);

            // This uses the mechanism to sync objects from server -> client
            // to make things easier, however this is running on the server,
            // so we'll ultimately move it there.
            var clientObject = new CoolSyncSubSystemForObjects.Client_Object
            {
                sys = sync,
                netId = netID,
                cur_packet_deltaid = 0,
                cur_packet = sync.base_packet.CloneViaSerialization(),
                last_receive_time = Time.realtimeSinceStartupAsDouble
            };

            reader.Get(out byte data1Size);
            reader.Get(out ushort data2Size);

            var position = reader.Position;
            reader.SetPosition(position + data1Size + data2Size);

            var numArray = new byte[bitsetSize];
            reader.GetBytes(numArray, bitsetSize);
            clientObject.bitset = new BitArray(numArray);

            reader.SetPosition(position);
            sync.Client_ReadData1(reader, data1Size, clientObject);

            reader.SetPosition(position + data1Size);
            sync.Client_ReadData2(reader, data2Size, clientObject);

            reader.SetPosition(position + data1Size + data2Size + bitsetSize);

            // Now that it exists, register it on the server.
            sync.server_objects[netID] = new CoolSyncSubSystemForObjects.Server_Object
            {
                netId = netID,
                cur_packet = clientObject.cur_packet,
                real_obj = clientObject.real_obj
            };
        }
    }
}