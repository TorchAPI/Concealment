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
        public static ConditionalWeakTable<MyEntity, IMyReplicable> _replicables = new ConditionalWeakTable<MyEntity, IMyReplicable>();

        public static IMyReplicable GetReplicable(MyEntity entity)
        {
            if (_replicables.TryGetValue(entity, out IMyReplicable rep))
                return rep;

            rep = MyExternalReplicable.FindByObject(entity);
            if(rep != null)
                _replicables.Add(entity, rep);
            return rep;
        }
    }
}
