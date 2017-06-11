using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Havok;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch;
using Torch.API;
using Torch.API.Plugins;
using Torch.Managers;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Concealment
{
    [Plugin("Concealment", "1.1", "17f44521-b77a-4e85-810f-ee73311cf75d")]
    public class ConcealmentPlugin : TorchPluginBase, IWpfPlugin
    {
        public Persistent<Settings> Settings { get; }
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
            Settings = Persistent<Settings>.Load("Concealment.cfg");
        }

        public UserControl GetControl()
        {
            return _control ?? (_control = new ConcealmentControl {DataContext = this});
        }

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            _concealedAabbTree = new MyDynamicAABBTreeD(MyConstants.GAME_PRUNING_STRUCTURE_AABB_EXTENSION);
            torch.SessionUnloading += Torch_SessionUnloading;
        }

        private void Torch_SessionUnloading()
        {
            RevealAll();
        }

        public override void Update()
        {
            if (MyAPIGateway.Session == null)
                return;

            if (_counter % Settings.Data.ConcealInterval == 0)
                ConcealDistantGrids(Settings.Data.ConcealDistance);
            if (_counter % Settings.Data.RevealInterval == 0)
                RevealNearbyGrids(Settings.Data.RevealDistance);
            _counter += 1;

            if (_init)
                return;

            //MySession.Static.Players.PlayerRequesting += RevealSpawns;
            MyMultiplayer.Static.ClientJoined += RevealCryoPod;

            _init = true;
        }

        public override void Dispose()
        {
            RevealAll();
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
                return;

            entity.GetStorage().SetValue(Id, "True");
#if !NOPHYS
            MyGamePruningStructure.Remove((MyEntity)entity);
            entity.Physics?.Deactivate();
#endif
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
                return;

            entity.GetStorage().SetValue(Id, "False");
#if !NOPHYS
            MyGamePruningStructure.Add((MyEntity)entity);
            entity.Physics?.Activate();
#endif
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

            Log.Debug($"Concealing grids: {group.GridNames}");
            group.Grids.ForEach(ConcealEntity);
#if !NOPHYS
            var aabb = group.WorldAABB;
            group.ProxyId = _concealedAabbTree.AddProxy(ref aabb, group, 0);
#endif
            group.Closing += Group_Closing;
            Task.Run(() =>
            {
                group.UpdatePostConceal();
                Log.Debug($"Group {group.Id} cached");
                group.IsConcealed = true;
                Torch.Invoke(() => _concealGroups.Add(group));
            });
            return group.Grids.Count;
        }

        private void Group_Closing(ConcealGroup group)
        {
            RevealGroup(group);
        }

        public int RevealGroup(ConcealGroup group)
        {
            if (!group.IsConcealed)
            {
                Log.Warn($"Attempted to reveal a group that wasn't concealed: {group.GridNames}");
                Log.Warn(new StackTrace());
                return 0;
            }
            Log.Debug($"Revealing grids: {group.GridNames}");
            group.Grids.ForEach(RevealEntity);
#if !NOPHYS
            _concealGroups.Remove(group);
            _concealedAabbTree.RemoveProxy(group.ProxyId);
#endif
            return group.Grids.Count;
        }

        public int RevealGridsInSphere(BoundingSphereD sphere)
        {
            var revealed = 0;
#if !NOPHYS
            _concealedAabbTree.OverlapAllBoundingSphere(ref sphere, _intersectGroups);
#else
            foreach (var group in ConcealGroups)
            {
                group.UpdateAABB();
                if (sphere.Contains(group.WorldAABB) != ContainmentType.Disjoint)
                    _intersectGroups.Add(group);
            }
#endif
            foreach (var group in _intersectGroups)
                revealed += RevealGroup(group);

            _intersectGroups.Clear();
            return revealed;
        }

        public int RevealNearbyGrids(double distanceFromPlayers)
        {
            //annoying log spam
            //Log.Debug("Revealing nearby grids");
            var revealed = 0;
            var playerSpheres = GetPlayerBoundingSpheres(distanceFromPlayers);
            foreach (var sphere in playerSpheres)
                revealed += RevealGridsInSphere(sphere);

            if (revealed != 0)
                Log.Info($"Revealed {revealed} grids near players.");
            return revealed;
        }

        public int ConcealDistantGrids(double distanceFromPlayers)
        {
            Log.Debug("Concealing distant grids");
            int concealed = 0;
            var playerSpheres = GetPlayerBoundingSpheres(distanceFromPlayers);

            ConcurrentBag<ConcealGroup> groups = new ConcurrentBag<ConcealGroup>();
            Parallel.ForEach(MyCubeGridGroups.Static.Physical.Groups, group =>
            {
                var concealGroup = new ConcealGroup(group);

                var volume = group.GetWorldAABB();
                if (playerSpheres.Any(s => s.Contains(volume) != ContainmentType.Disjoint))
                    return;

                //if (IsExcluded(concealGroup))
                //    return;

                groups.Add(concealGroup);
            });
            foreach (var group in groups)
            {
                concealed += ConcealGroup(group);
            }

            if (concealed != 0)
                Log.Info($"Concealed {concealed} grids distant from players.");

            return concealed;
        }

        public bool IsExcluded(ConcealGroup group)
        {
            foreach (var block in group.Grids.SelectMany(g => g.CubeBlocks).Select(x => x.FatBlock))
            {
                if (block == null)
                    continue;
                if (Settings.Data.ExcludedSubtypes.Contains(block.BlockDefinition.Id.SubtypeName))
                    return false;
            }

            return true;
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

    public static class Extensions
    {
        public static MyModStorageComponentBase GetStorage(this IMyEntity entity)
        {
            return entity.Storage = entity.Storage ?? new MyModStorageComponent();
        }
    }
}