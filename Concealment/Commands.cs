using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using VRageMath;

namespace Concealment
{
    public class Commands : CommandModule
    {
        public ConcealmentPlugin Plugin => (ConcealmentPlugin)Context.Plugin;

        [Command("conceal", "Conceal grids x distance from players."), Permission(MyPromoteLevel.SpaceMaster)]
        public void Conceal(double distance = 0)
        {
            if (distance == 0)
            {
                distance = Plugin.Settings.ConcealDistance;
            }
            var num = Plugin.ConcealDistantGrids(distance);
            Context.Respond($"{num} grids concealed.");
        }

        [Command("reveal", "Reveal all grids within the given distance"), Permission(MyPromoteLevel.SpaceMaster)]
        public void Reveal(double distance = 1000)
        {
            var pos = Context.Player.Controller.ControlledEntity?.Entity.GetPosition();
            if (!pos.HasValue)
            {
                Context.Respond("You must be controlling an entity");
                return;
            }

            var sphere = new BoundingSphereD(pos.Value, distance);
            var num = Plugin.RevealGridsInSphere(sphere);
            Context.Respond($"{num} grids revealed.");
        }

        [Command("all", "Reveal all grids", null, "reveal"), Permission(MyPromoteLevel.SpaceMaster)]
        public void RevealAll()
        {
            int num = Plugin.RevealAll();
            Context.Respond($"{num} grids revealed.");
        }
    }
}