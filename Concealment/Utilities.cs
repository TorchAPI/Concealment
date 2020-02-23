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
        public static IMyReplicable GetReplicable(MyEntity entity)
        {
            return MyExternalReplicable.FindByObject(entity);
        }
    }
}
