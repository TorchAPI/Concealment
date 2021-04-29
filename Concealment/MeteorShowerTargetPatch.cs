using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ParallelTasks;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using Torch.Utils;

namespace Concealment
{
    public static class MeteorShowerTargetPatch
    {
        [ReflectedMethodInfo(typeof(MeteorShowerTargetPatch), nameof(Transpiler))]
#pragma warning disable 649
        private static readonly MethodInfo _transpilerMethod;

        [ReflectedMethodInfo(typeof(MeteorShowerTargetPatch), nameof(FixTargets))]
        private static readonly MethodInfo _fixTargetsMethod;
#pragma warning restore 649

        private static ConcealmentPlugin _plugin;

        public static void Patch(PatchContext ctx, ConcealmentPlugin plugin)
        {
            _plugin = plugin;
            ctx.GetPattern(typeof(MyMeteor).Assembly.GetType("Sandbox.Game.Entities.MyMeteorShower", true)
                    .GetMethod("GetTargets", BindingFlags.Static | BindingFlags.NonPublic)).Transpilers
                .Add(_transpilerMethod);
        }

        private static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> ins)
        {
            var found = false;
            foreach (var instruction in ins)
            {
                if (!found && instruction.OpCode == OpCodes.Stloc_0)
                {
                    found = true;
                    yield return instruction;
                    yield return new MsilInstruction(OpCodes.Ldloc_0);
                    yield return new MsilInstruction(OpCodes.Call).InlineValue(_fixTargetsMethod);
                    continue;
                }

                yield return instruction;
            }
        }

        private static void FixTargets(List<MyCubeGrid> grids)
        {
            // idk about that, just shitty coded thing 
            var toRemove = new List<MyCubeGrid>();
            Parallel.ForEach(_plugin.ConcealedGroups.SelectMany(b => b.Grids), grid =>
            {
                if (grids.Contains(grid))
                    toRemove.Add(grid);
            }, blocking: true);
            toRemove.ForEach(b => grids.Remove(b));
        }
    }
}