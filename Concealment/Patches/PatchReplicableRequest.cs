using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NLog;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage;
using VRage.Network;

namespace Concealment.Patches
{
    /// <summary>
    /// Clients call ReplicableRequest when they need a replicable immediately (remote terminal, remote control, etc).
    /// We listen to this even and immediately reveal the grid if concealed.
    ///
    /// Event is also called when client no longer needs the replicable, but we can just let the grid re-conceal
    /// when the timer elapses.
    /// </summary>
    [PatchShim]
    static class PatchReplicableRequest
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        [ReflectedMethodInfo(typeof(MyReplicationServer), nameof(MyReplicationServer.ReplicableRequest))]
        private static MethodInfo _requestMethod;

        public static void Patch(PatchContext ctx)
        {
            //temporarily disabled because BitStream.ResetRead is throwing dumbass exceptions
            //ctx.GetPattern(_requestMethod).Prefixes.Add(typeof(PatchReplicableRequest).GetMethod(nameof(PrefixRequest)));
        }

        public static void PrefixRequest(MyPacket packet)
        {
            try
            {
                var stream = packet.BitStream;
                var id = stream.ReadInt64();
                bool add = stream.ReadBool();

                var g = ConcealmentPlugin.Instance.ConcealedGroups.First(gr => gr.Grids.Any(q => q.EntityId == id));
                if (g != null && add)
                    ConcealmentPlugin.Instance.RevealGroup(g);

                stream.ResetRead();
            }
            catch (Exception ex)
            {
                Log.Error("Exception encountered in Request patch!");
                Log.Error(ex);
            }
            finally
            {
                //this allows us to fail gracefully
                packet.BitStream.ResetRead();
            }
        }
    }
}
