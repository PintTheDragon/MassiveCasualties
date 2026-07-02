using CUCoreLib.Data;
using CUCoreLib.Helpers;
using CUCoreLib.Registries;
using MassiveCasualties.Behaviors;

namespace MassiveCasualties.Objects;

internal static class Teleporter
{
    internal static void Register()
    {
        BuildingEntityRegistry.Register("mc_teleporter", new CustomBuildingEntityDefinition
        {
            Name = "Improvised Lift",
            Description = "Where does it go?",
            Sprite = AssetLoader.LoadEmbeddedSprite("Images.teleporter.png"),
            Health = 500f,
            Metallic = true,
            HitSoundReferenceId = "metal",
            Placement = BuildingPlacementType.Floor,
            GenerationStyle = BuildingGenerationStyle.Standard,
            SpawnMinPerChunk = 0.7f,
            SpawnMaxPerChunk = 1.0f,
            SurfaceOffset = 3.5f,
            Components = [typeof(TeleporterScript)],
            ItemsDropOnDestroy =
            [
                BuildingEntityRegistry.AddDrop("scrapmetal", 1f, 0.8f)
            ]
        });
    }
}