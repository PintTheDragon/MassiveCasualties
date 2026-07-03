using System.Reflection;
using KrokoshaCasualtiesMP;
using Unity.VisualScripting;

namespace MassiveCasualties.Network;

/// <summary>
///     Network packet IDs.
/// </summary>
internal enum MessageType : ushort
{
    WorldPlacePlayerWithSave = 1500,
    GetTeleporterLobby = 1501,
    ForwardLobby = 1502
}

internal static class NetRegistration
{
    /// <summary>
    ///     Registers all net receivers in the plugin.
    /// </summary>
    internal static void Register()
    {
        foreach (var type in Assembly.GetAssembly(typeof(NetRegistration)).GetTypesSafely())
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic |
                                                   BindingFlags.Public | BindingFlags.Static))
            {
                var customAttributes =
                    method.GetCustomAttributes(typeof(KrokoshaNetworkMessageReceiverAttribute), true);
                if (customAttributes.Length == 0) continue;

                var key = (KrokoshaNetworkMessageReceiverAttribute)customAttributes[0];
                var value = (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)method.CreateDelegate(
                    typeof(KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate));
                KrokoshaScavMultiplayer.all_network_receivers_from_attributes.Add(key, value);
            }
        }
    }
}