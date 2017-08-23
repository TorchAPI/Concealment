using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Havok;
using NLog;
using Sandbox.Definitions;
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
using VRage.Game.Definitions;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ModAPI;
using VRageMath;

namespace Concealment
{
    [Plugin("Concealment", "1.2.1", "17f44521-b77a-4e85-810f-ee73311cf75d")]
    public sealed class ConcealmentPlugin : TorchPluginBase, IWpfPlugin
    {
        public Persistent<Settings> Settings { get; private set; }
        public ObservableList<ConcealGroup> ConcealedGroups { get; } = new ObservableList<ConcealGroup>();

        private readonly Dictionary<long, Timer> _keepAliveTimers = new Dictionary<long, Timer>();
        private static readonly Logger Log = LogManager.GetLogger("Concealment");
        private UserControl _control;
        private ulong _counter;
        private bool _init;
        private readonly List<ConcealGroup> _intersectGroups;
        private MyDynamicAABBTreeD _concealedAabbTree;
        private bool _settingsChanged;
        private bool _ready;

        public ConcealmentPlugin()
        {
            _intersectGroups = new List<ConcealGroup>();
        }

        UserControl IWpfPlugin.GetControl()
        {
            return _control ?? (_control = new ConcealmentControl {DataContext = this});
        }

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Settings = Persistent<Settings>.Load(Path.Combine(StoragePath, "Concealment.cfg"));
            Settings.Data.PropertyChanged += Data_PropertyChanged;
            _concealedAabbTree = new MyDynamicAABBTreeD(MyConstants.GAME_PRUNING_STRUCTURE_AABB_EXTENSION);
            RegisterEntityStorage("Concealment", Id);
        }

        private void Data_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _settingsChanged = true;
        }

        private void RegisterEntityStorage(string name, Guid id)
        {
            var comp = new MyModStorageComponentDefinition
            {
                Id = new MyDefinitionId(typeof(MyObjectBuilder_ModStorageComponent), name),
                RegisteredStorageGuids = new[] { id }
            };
            MyDefinitionManager.Static.Definitions.AddDefinition(comp);
        }

        //TODO: divide conceal/reveal runs over several ticks to avoid stuttering.
        public override void Update()
        {
            if (MyAPIGateway.Session == null || !Settings.Data.Enabled)
                return;

            if (_ready)
            {
                if (_counter % (ulong)Settings.Data.ConcealInterval == 0)
                    ConcealGrids(Settings.Data.ConcealDistance);
                if (_counter % (ulong)Settings.Data.RevealInterval == 0)
                    RevealGrids(Settings.Data.RevealDistance);
                _counter += 1;
            }

            if (_init || MyAPIGateway.TerminalControls == null)
                return;

            //Make sure the game physics has time to initialize.
            var delayTimer = new System.Timers.Timer
            {
                AutoReset = false,
                Interval = 5000,
            };

            delayTimer.Elapsed += (sender, args) => _ready = true;
            delayTimer.Start();

            var keepAliveAction = MyAPIGateway.TerminalControls.CreateAction<IMyRemoteControl>("Concealment.KeepAlive");
            keepAliveAction.Action = KeepAlive;
            MyAPIGateway.TerminalControls.AddAction<IMyRemoteControl>(keepAliveAction);

            MyMultiplayer.Static.ClientJoined += RevealCryoPod;

            _init = true;
        }

        private void KeepAlive(IMyTerminalBlock block)
        {
            var rc = (IMyRemoteControl)block;

            if (rc.CubeGrid.IsStatic)
                return;

            Log.Debug($"Keepalive triggered on grid {block.CubeGrid.DisplayName}");
            var dueTime = TimeSpan.FromSeconds(Settings.Data.ConcealInterval / 60d);
            if (_keepAliveTimers.TryGetValue(block.CubeGrid.EntityId, out Timer timer))
            {
                timer.Change(dueTime, TimeSpan.Zero);
            }
            else
            {
                var newTimer = new Timer(KeepAliveCallback, block.CubeGrid.EntityId, dueTime, TimeSpan.Zero);
                _keepAliveTimers.Add(block.CubeGrid.EntityId, newTimer);
            }
        }

        private void KeepAliveCallback(object state)
        {
            var grid = (long)state;
            Log.Debug($"Keepalive expired on grid {grid}");
            Torch.Invoke(() =>
            {
                var timer = _keepAliveTimers[grid];
                timer.Dispose();
                _keepAliveTimers.Remove(grid);
            });
        }

        public override void Dispose()
        {
            //RevealAll();
            Settings.Save("Concealment.cfg");
        }

        public void GetConcealedGrids(List<IMyCubeGrid> grids)
        {
            ConcealedGroups.SelectMany(x => x.Grids).ForEach(grids.Add);
        }

        private void RevealCryoPod(ulong steamId)
        {
            Torch.Invoke(() =>
            {
                Log.Debug(nameof(RevealCryoPod));
                for (var i = ConcealedGroups.Count - 1; i >= 0; i--)
                {
                    var group = ConcealedGroups[i];

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

                for (var i = ConcealedGroups.Count - 1; i >= 0; i--)
                {
                    var group = ConcealedGroups[i];

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
                    UnregisterRecursive(child.Container.Entity);
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
                    RegisterRecursive(child.Container.Entity);
            }
        }

        private int ConcealGroup(ConcealGroup group)
        {
            if (ConcealedGroups.Any(g => g.Id == group.Id))
                return 0;

            Log.Debug($"Concealing grids: {group.GridNames}");
            group.Grids.ForEach(ConcealEntity);
#if !NOPHYS
            group.UpdateAABB();
            var aabb = group.WorldAABB;
            group.ProxyId = _concealedAabbTree.AddProxy(ref aabb, group, 0);
#endif
            group.Closing += Group_Closing;
            Task.Run(() =>
            {
                group.UpdatePostConceal();
                Log.Debug($"Group {group.Id} cached");
                group.IsConcealed = true;
                Torch.Invoke(() => ConcealedGroups.Add(group));
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

            var count = group.Grids.Count;
            Log.Debug($"Revealing grids: {group.GridNames}");
            group.Grids.ForEach(RevealEntity);
#if !NOPHYS
            ConcealedGroups.Remove(group);
            _concealedAabbTree.RemoveProxy(group.ProxyId);
            group.UpdatePostReveal();
#endif
            return count;
        }

        public int RevealGridsInSphere(BoundingSphereD sphere)
        {
            var revealed = 0;
#if !NOPHYS
            _concealedAabbTree.OverlapAllBoundingSphere(ref sphere, _intersectGroups);
#else
            foreach (var group in ConcealedGroups)
            {
                group.UpdateAABB();
                if (sphere.Contains(group.WorldAABB) != ContainmentType.Disjoint)
                    _intersectGroups.Add(group);
            }
#endif
            Log.Trace($"{_intersectGroups.Count} groups");
            foreach (var group in _intersectGroups)
                revealed += RevealGroup(group);

            _intersectGroups.Clear();
            return revealed;
        }

        public int RevealGrids(double distanceFromPlayers)
        {
            var revealed = 0;
            var playerSpheres = GetPlayerViewSpheres(distanceFromPlayers);
            foreach (var sphere in playerSpheres)
            {
                Log.Trace($"{sphere.Center}: {sphere.Radius}");
                revealed += RevealGridsInSphere(sphere);
            }

            if (_settingsChanged)
            {
                for (var i = ConcealedGroups.Count - 1; i >= 0; i--)
                {
                    if (IsExcluded(ConcealedGroups[i]))
                        revealed += RevealGroup(ConcealedGroups[i]);
                }

                _settingsChanged = false;
            }

            if (revealed != 0)
                Log.Info($"Revealed {revealed} grids near players.");
            return revealed;
        }

        public int ConcealGrids(double distanceFromPlayers = 0)
        {
            Log.Debug("Concealing grids");
            int concealed = 0;
            var playerSpheres = GetPlayerViewSpheres(distanceFromPlayers);

            ConcurrentBag<ConcealGroup> groups = new ConcurrentBag<ConcealGroup>();
            var sw = Stopwatch.StartNew();
            Parallel.ForEach(MyCubeGridGroups.Static.Physical.Groups, group =>
            {
                var concealGroup = new ConcealGroup(group);

                if (distanceFromPlayers != 0)
                {
                    var volume = group.GetWorldAABB();
                    if (playerSpheres.Any(s => s.Contains(volume) != ContainmentType.Disjoint))
                    {
                        Log.Trace("group near player");
                        return;
                    }
                }

                if (IsExcluded(concealGroup))
                {
                    Log.Trace("group excluded");
                    return;
                }

                groups.Add(concealGroup);
            });
            Log.Debug($"Scanned grids in {sw.ElapsedMilliseconds}ms.");
            sw.Restart();

            foreach (var group in groups)
            {
                concealed += ConcealGroup(group);
            }
            Log.Debug($"Concealed grids in {sw.ElapsedMilliseconds}ms.");
            sw.Stop();


            if (concealed != 0)
                Log.Info($"Concealed {concealed} grids distant from players.");

            return concealed;
        }

        public bool IsExcluded(ConcealGroup group)
        {
            var pirateId = MyPirateAntennas.GetPiratesId();
            foreach (var grid in group.Grids)
            {
                if (_keepAliveTimers.ContainsKey(grid.EntityId))
                {
                    Log.Trace($"{group.GridNames} is kept alive by PB action");
                    return true;
                }

                if (!Settings.Data.ConcealPirates && grid.BigOwners.Contains(pirateId))
                {
                    Log.Trace($"{group.GridNames} is kept alive by pirate ownership");
                    return true;
                }
            }

            var exclude = false;
            Parallel.ForEach(group.Grids, grid =>
            {
                foreach (var block in grid.CubeBlocks.Select(x => x.FatBlock))
                {
                    if (block == null)
                        continue;

                    if (block is IMyProductionBlock p && !Settings.Data.ConcealProduction && p.IsProducing)
                    {
                        Log.Trace($"{group.GridNames} exempted production ({p.CustomName} active)");
                        exclude = true;
                        break;
                    }

                    if (Settings.Data.ExcludedSubtypes.Contains(block.BlockDefinition.Id.SubtypeName))
                    {
                        Log.Trace($"{group.GridNames} exempted subtype {block.BlockDefinition.Id.SubtypeName}");
                        exclude = true;
                        break;
                    }
                }
            });

            return exclude;
        }

        public int RevealAll()
        {
            Log.Debug("Revealing all grids");

            var revealed = 0;
            for (var i = ConcealedGroups.Count - 1; i >= 0; i--)
                revealed += RevealGroup(ConcealedGroups[i]);

            return revealed;
        }

        private List<BoundingSphereD> GetPlayerViewSpheres(double distance)
        {
            return ((MyPlayerCollection)MyAPIGateway.Multiplayer.Players).GetOnlinePlayers().Select(p => new BoundingSphereD(p.GetPosition(), distance)).ToList();
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