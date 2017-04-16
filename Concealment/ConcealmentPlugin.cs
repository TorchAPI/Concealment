using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch;
using Torch.API;
using Torch.API.Plugins;
using Torch.Managers;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Concealment
{
    [Plugin("Concealment", "1.0", "17f44521-b77a-4e85-810f-ee73311cf75d")]
    public class ConcealmentPlugin : TorchPluginBase, IWpfPlugin
    {
        public Settings Settings { get; }
        public MTObservableCollection<ConcealGroup> ConcealGroups { get; } = new MTObservableCollection<ConcealGroup>();

        private static readonly Logger Log = LogManager.GetLogger("Concealment");
        private UserControl _control;
        private ulong _counter;
        private bool _init;
        private readonly List<ConcealGroup> _concealGroups = new List<ConcealGroup>();
        private readonly List<ConcealGroup> _intersectGroups;
        private MyDynamicAABBTreeD _concealedAabbTree;


        public ConcealmentPlugin()
        {
            _intersectGroups = new List<ConcealGroup>();
            Settings = Settings.LoadOrCreate("Concealment.cfg");
        }

        public UserControl GetControl()
        {
            return _control ?? (_control = new ConcealmentControl {DataContext = this});
        }

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            _concealedAabbTree = new MyDynamicAABBTreeD(MyConstants.GAME_PRUNING_STRUCTURE_AABB_EXTENSION);
        }

        public override void Update()
        {
            if (MyAPIGateway.Session == null)
                return;

            if (_counter % Settings.ConcealInterval == 0)
                ConcealDistantGrids(Settings.ConcealDistance);
            if (_counter % Settings.RevealInterval == 0)
                RevealNearbyGrids(Settings.RevealDistance);
            _counter += 1;

            if (_init)
                return;

            MySession.Static.Players.PlayerRequesting += RevealSpawns;
            MyMultiplayer.Static.ClientJoined += RevealCryoPod;

            _init = true;
        }

        public override void Dispose()
        {
            Settings.Save("Concealment.cfg");
        }

        public void GetConcealedGrids(List<IMyCubeGrid> grids)
        {
            _concealGroups.SelectMany(x => x.Grids).ForEach(grids.Add);
        }

        private void RevealCryoPod(ulong steamId)
        {
            Torch.Invoke(() =>
            {
                Log.Debug(nameof(RevealCryoPod));
                for (var i = _concealGroups.Count - 1; i >= 0; i--)
                {
                    var group = _concealGroups[i];

                    if (group.IsCryoOccupied(steamId))
                    {
                        RevealGroup(group);
                        return;
                    }
                }
            });
        }

        private void RevealSpawns(PlayerRequestArgs args)
        {
            Torch.Invoke(() =>
            {
                Log.Debug(nameof(RevealSpawns));
                var identityId = MySession.Static.Players.TryGetIdentityId(args.PlayerId.SteamId);
                if (identityId == 0)
                    return;

                for (var i = _concealGroups.Count - 1; i >= 0; i--)
                {
                    var group = _concealGroups[i];

                    if (group.IsMedicalRoomAvailable(identityId))
                        RevealGroup(group);
                }
            });
        }

        private void ConcealEntity(IMyEntity entity)
        {
            if (entity != entity.GetTopMostParent())
                throw new InvalidOperationException("Can only conceal top-level entities.");

            MyGamePruningStructure.Remove((MyEntity)entity);
            entity.Physics?.Deactivate();
            UnregisterRecursive(entity);

            void UnregisterRecursive(IMyEntity e)
            {
                MyEntities.UnregisterForUpdate((MyEntity)e);
                if (e.Hierarchy == null)
                    return;

                foreach (var child in e.Hierarchy.Children)
                    UnregisterRecursive(child.Entity);
            }
        }

        private void RevealEntity(IMyEntity entity)
        {
            if (entity != entity.GetTopMostParent())
                throw new InvalidOperationException("Can only conceal top-level entities.");

            MyGamePruningStructure.Add((MyEntity)entity);
            entity.Physics?.Activate();
            RegisterRecursive(entity);

            void RegisterRecursive(IMyEntity e)
            {
                MyEntities.RegisterForUpdate((MyEntity)e);
                if (e.Hierarchy == null)
                    return;

                foreach (var child in e.Hierarchy.Children)
                    RegisterRecursive(child.Entity);
            }
        }

        private int ConcealGroup(ConcealGroup group)
        {
            if (_concealGroups.Any(g => g.Id == group.Id))
                return 0;

            Log.Info($"Concealing grids: {string.Join(", ", group.Grids.Select(g => g.DisplayName))}");
            group.ConcealTime = DateTime.Now;
            group.Grids.ForEach(ConcealEntity);
            Task.Run(() =>
            {
                group.UpdatePostConceal();
                var aabb = group.WorldAABB;
                group.ProxyId = _concealedAabbTree.AddProxy(ref aabb, group, 0);
                Log.Debug($"Group {group.Id} cached");
                Torch.Invoke(() => _concealGroups.Add(group));
            });
            return group.Grids.Count;
        }

        public int RevealGroup(ConcealGroup group)
        {
            Log.Info($"Revealing grids: {string.Join(", ", group.Grids.Select(g => g.DisplayName))}");
            group.Grids.ForEach(RevealEntity);
            _concealGroups.Remove(group);
            _concealedAabbTree.RemoveProxy(group.ProxyId);
            return group.Grids.Count;
        }

        public int RevealGridsInSphere(BoundingSphereD sphere)
        {
            var revealed = 0;
            _concealedAabbTree.OverlapAllBoundingSphere(ref sphere, _intersectGroups);
            foreach (var group in _intersectGroups)
                revealed += RevealGroup(group);

            _intersectGroups.Clear();
            return revealed;
        }

        public int RevealNearbyGrids(double distanceFromPlayers)
        {
            Log.Debug("Revealing nearby grids");
            var revealed = 0;
            var playerSpheres = GetPlayerBoundingSpheres(distanceFromPlayers);
            foreach (var sphere in playerSpheres)
                revealed += RevealGridsInSphere(sphere);

            return revealed;
        }

        public int ConcealDistantGrids(double distanceFromPlayers)
        {
            Log.Debug("Concealing distant grids");
            var concealed = 0;
            var playerSpheres = GetPlayerBoundingSpheres(distanceFromPlayers);

            foreach (var group in MyCubeGridGroups.Static.Physical.Groups)
            {
                var volume = group.GetWorldAABB();
                if (playerSpheres.Any(s => s.Contains(volume) != ContainmentType.Disjoint))
                    continue;

                concealed += ConcealGroup(new ConcealGroup(group));
            }

            return concealed;
        }

        public int RevealAll()
        {
            Log.Debug("Revealing all grids");

            var revealed = 0;
            for (var i = _concealGroups.Count - 1; i >= 0; i--)
                revealed += RevealGroup(_concealGroups[i]);

            return revealed;
        }

        private List<BoundingSphereD> GetPlayerBoundingSpheres(double distance)
        {
            return ((MyPlayerCollection)MyAPIGateway.Multiplayer.Players).GetOnlinePlayers().Where(p => p.Controller?.ControlledEntity != null).Select(p => new BoundingSphereD(p.Controller.ControlledEntity.Entity.PositionComp.GetPosition(), distance)).ToList();
        }
    }
}