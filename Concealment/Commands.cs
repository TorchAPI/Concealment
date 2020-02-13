using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using VRageMath;

namespace Concealment
{
    public class Commands : CommandModule
    {
        public ConcealmentPlugin Plugin => (ConcealmentPlugin)Context.Plugin;

        [Command("update", "Forces a concealment update immediately."), Permission(MyPromoteLevel.SpaceMaster)]
        public void Conceal()
        {
            var num = Plugin.ConcealGrids();
            Context.Respond($"{num.Item1} grids concealed, {num.Item2} grids revealed");
        }
        
        [Command("reveal all", "Reveal all grids"), Permission(MyPromoteLevel.SpaceMaster)]
        public void RevealAll()
        {
            int num = Plugin.RevealAll();
            Context.Respond($"{num} grids revealed.");
        }

        [Command("conceal on", "Enable concealment.")]
        public void Enable()
        {
            Plugin.Settings.Data.Enabled = true;
            Plugin.ConcealGrids();
        }

        [Command("conceal off", "Disable concealment.")]
        public void Disable()
        {
            Plugin.Settings.Data.Enabled = false;
            Plugin.RevealAll();
        }
    }
}