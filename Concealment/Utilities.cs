using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Sandbox.Game.Replication;
using VRage.Game.Entity;
using VRage.Network;

namespace Concealment
{
    public static class Utilities
    {
        private static ConditionalWeakTable<MyEntity, IMyReplicable> _replicables = new ConditionalWeakTable<MyEntity, IMyReplicable>();
        public static IMyReplicable GetReplicable(MyEntity entity)
        {
            lock (_replicables)
            {
                if (!_replicables.TryGetValue(entity, out IMyReplicable rep))
                {
                    rep = MyExternalReplicable.FindByObject(entity);
                    if (rep != null)
                        _replicables.Add(entity, rep);
                }

                return rep;
            }
        }

        public static bool IsReplicatedSafe(this MyReplicationServer server, IMyReplicable replicable)
        {
            if (replicable == null)
                return true; //fail true so that grids are revealed in error conditions

            return server.IsReplicated(replicable);
        }
    }
}
