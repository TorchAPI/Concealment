// #define DEBUG_DYNAMIC_CONCEAL_OWNERSHIP

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using ParallelTasks;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Torch.API;
using Torch.Managers;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Generics;
using VRage.Groups;
using VRage.ObjectBuilders;
using VRageMath;
using Task = ParallelTasks.Task;

namespace Concealment
{
    public class DynamicConcealmentManager : Manager
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();
        private static readonly TimeSpan _rebalanceTiming = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan _rebuildNearbyList = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan _rebuildConcealState = TimeSpan.FromSeconds(10);

        [Dependency] private readonly PatchManager _patchManager;

        private PatchContext _ctx;

        private readonly ConcealmentPlugin _plugin;
        private readonly Timer _refreshCollection;

        public DynamicConcealmentManager(ConcealmentPlugin plugin, ITorchBase torch) : base(torch)
        {
            _plugin = plugin;
            _refreshCollection = new Timer(RefreshFromCollection);
        }

        ~DynamicConcealmentManager()
        {
            _refreshCollection?.Dispose();
        }

#pragma warning disable 649
        [ReflectedGetter(Name = "m_objectFactory",
            TypeName = "Sandbox.Game.Entities.Cube.MyCubeBlockFactory, Sandbox.Game")]
        private static readonly Func<MyObjectFactory<MyCubeBlockTypeAttribute, object>> _cubeBlockFactory;
#pragma warning restore 649

        /// <inheritdoc/>
        public override void Attach()
        {
            if (_ctx == null)
                _ctx = _patchManager.AcquireContext();
            RefreshFromCollection(null);
            _patchManager.Commit();
        }


        private void DynamicConcealment_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            _refreshCollection.Change(1000 * 30, -1);
        }

        private void EntryOnPropertyChanged(object sender, PropertyChangedEventArgs propertyChangedEventArgs)
        {
            _refreshCollection.Change(1000 * 30, -1);
        }

        private void DetachCollection()
        {
            _plugin.Settings.Data.DynamicConcealment.CollectionChanged -= DynamicConcealment_CollectionChanged;
            foreach (var entry in _plugin.Settings.Data.DynamicConcealment)
                entry.PropertyChanged -= EntryOnPropertyChanged;
        }

        private void AttachCollection()
        {
            _plugin.Settings.Data.DynamicConcealment.CollectionChanged += DynamicConcealment_CollectionChanged;
            foreach (var entry in _plugin.Settings.Data.DynamicConcealment)
                entry.PropertyChanged += EntryOnPropertyChanged;
        }

        private void RefreshFromCollection(object _)
        {
            DetachCollection();
            AttachCollection();
            RebuildFromSettings(_plugin.Settings.Data.DynamicConcealment);

            foreach (var type in _genericConfig.Keys.Concat(_config.Keys.Select(x => x.TypeId)).Distinct())
            {
                try
                {
                    var res = _cubeBlockFactory().TryGetProducedType(type);
                    if (res == null)
                    {
                        _log.Warn($"Unable to determine entity type of OB type {type}");
                        continue;
                    }
                    if (!typeof(MyEntity).IsAssignableFrom(res))
                    {
                        _log.Warn($"Type {res}, OB {type} isn't assignable to entity");
                        continue;
                    }
                    DoPatch(res);
                }
                catch (Exception e)
                {
                    _log.Error(e, $"Failed to attach to {type}");
                }
            }
        }

        /// <inheritdoc/>
        public override void Detach()
        {
            DetachCollection();
            _patchManager.FreeContext(_ctx);
        }

        private static readonly string[] _patchTargets =
        {
            nameof(MyEntity.UpdateBeforeSimulation), nameof(MyEntity.UpdateBeforeSimulation10),
            nameof(MyEntity.UpdateBeforeSimulation100),
            nameof(MyEntity.UpdateAfterSimulation), nameof(MyEntity.UpdateAfterSimulation10),
            nameof(MyEntity.UpdateAfterSimulation100)
        };

        private static readonly MethodInfo _prefixUpdateMethod =
            typeof(DynamicConcealmentManager).GetMethod(nameof(PrefixUpdate),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) ??
            throw new Exception("Failed to find patch method");

        private void DoPatch(Type type)
        {
            while (true)
            {
                var patched = 0;
                foreach (var name in _patchTargets)
                {
                    var target = type.GetMethod(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (target != null)
                    {
                        // nop if it's already added.
                        if (_ctx.GetPattern(target).Prefixes.Add(_prefixUpdateMethod))
                            patched++;
                    }
                }
                if (patched > 0)
                    _log.Debug($"Attached dynamic concealment to {patched} new in {type}");
                if (type.BaseType == null || !typeof(MyEntity).IsAssignableFrom(type.BaseType))
                    break;
                type = type.BaseType;
            }
        }

        private class DynamicConcealmentGridInfo : IPrioritizedWork
        {
            private readonly WeakReference<MyCubeGrid> _ref;

            internal DynamicConcealmentGridInfo(MyCubeGrid block)
            {
                _ref = new WeakReference<MyCubeGrid>(block);
            }

            internal readonly ReaderWriterLockSlim Lock = new ReaderWriterLockSlim();
            internal int ConfigVersion = -1;
            internal double NextQueryDistance;
            internal readonly List<GCHandle> NearbyEntities = new List<GCHandle>();
            internal DateTime LastNearbyUpdate;
            private Task _task;

            ~DynamicConcealmentGridInfo()
            {
                foreach (var k in NearbyEntities)
                    k.Free();
                NearbyEntities.Clear();
            }

            public void DoWork(WorkData workData = null)
            {
                if (!_ref.TryGetTarget(out MyCubeGrid ent))
                    return;
                BalanceTick(ent);

                var aabb = ent.PositionComp.WorldAABB.Inflate(NextQueryDistance);

                _entityListPool.AllocateOrCreate(out var list);
                if (list == null) list = new List<MyEntity>();
                try
                {
                    list.Clear();
                    MyGamePruningStructure.GetTopMostEntitiesInBox(ref aabb, list);
                    using (Lock.WriteUsing())
                    {
                        foreach (var k in NearbyEntities)
                            k.Free();
                        NearbyEntities.Clear();
                        foreach (var e in list)
                            NearbyEntities.Add(GCHandle.Alloc(e, GCHandleType.Weak));
                    }
                }
                finally
                {
                    _entityListPool.Deallocate(list);
                }
                LastNearbyUpdate = DateTime.Now;
            }

            public WorkOptions Options { get; } = new WorkOptions() {MaximumThreads = 1};

            public void ScheduleRefresh()
            {
                lock (this)
                {
                    if (_task.IsComplete)
                        _task = ParallelTasks.Parallel.Start(this);
                }
            }

            public WorkPriority Priority => WorkPriority.Low;
        }

        private class DynamicConcealmentBlockInfo : IPrioritizedWork
        {
            private readonly WeakReference<MyCubeBlock> _ref;

            internal DynamicConcealmentBlockInfo(MyCubeBlock block)
            {
                _ref = new WeakReference<MyCubeBlock>(block);
            }

            internal int ConfigVersion = -1;
            internal bool ConcealState;
            internal DynamicConcealmentTargetInfo Config;
            internal DateTime LastConcealStateUpdate;

            private Task _task;


            public void DoWork(WorkData workData = null)
            {
                if (!_ref.TryGetTarget(out MyCubeBlock block))
                    return;
                var gridInfo = GetConcealInfo(block.CubeGrid);

                // associate these queries per-grid
                gridInfo.NextQueryDistance = Math.Max(gridInfo.NextQueryDistance, Config.MaxDistance);
                if (gridInfo.LastNearbyUpdate + _rebuildNearbyList < DateTime.Now)
                {
                    gridInfo.ScheduleRefresh();
                }

#if DEBUG_DYNAMIC_CONCEAL_OWNERSHIP
                long[] ownerInfo = new long[2 * (int) Settings.DynamicConcealType.None];
#endif
                double[] info = new double[(int) Settings.DynamicConcealType.None];
                for (var k = 0; k < info.Length; k++)
                    info[k] = double.MaxValue;
                using (gridInfo.Lock.ReadUsing())
                    foreach (var eid in gridInfo.NearbyEntities)
                    {
                        MyEntity ent = eid.Target as MyEntity;
                        if (ent == null) continue;

                        Settings.DynamicConcealType type = Settings.DynamicConcealType.None;
                        long causingOwner = 0;
                        if (ent is MyCubeGrid grid && grid != block.CubeGrid)
                        {
                            var worstRelation = 0;
                            foreach (var otherOwner in grid.SmallOwners)
                            {
                                var relation = (int) GetRelationTolerant(block.OwnerId, otherOwner);
                                if (relation > worstRelation)
                                {
                                    causingOwner = otherOwner;
                                    worstRelation = relation;
                                }
                            }
                            type = GetConcealType((MyRelationsBetweenPlayerAndBlock) worstRelation, true);
                        }
                        else if (ent is MyCharacter character)
                        {
                            causingOwner = character.GetPlayerIdentityId();
                            type = GetConcealType(
                                GetRelationTolerant(block.OwnerId, character.GetPlayerIdentityId()),
                                false);
                        }
                        if (type != Settings.DynamicConcealType.None)
                        {
                            var dist = Vector3D.DistanceSquared(ent.PositionComp.WorldVolume.Center,
                                block.PositionComp.WorldVolume.Center);
                            if (dist < info[(int) type])
                            {
                                info[(int) type] = dist;
#if DEBUG_DYNAMIC_CONCEAL_OWNERSHIP
                                ownerInfo[2 * (int) type] = causingOwner;
                                ownerInfo[2 * (int) type + 1] = ent.EntityId;
#endif
                            }
                        }
                    }

                var concealed = true;
                Settings.DynamicConcealType reason = Settings.DynamicConcealType.None;
#if DEBUG_DYNAMIC_CONCEAL_OWNERSHIP
                var distanceFound = 0D;
                var distanceConfig = 0D;
                var reasonOwner = 0L;
                var reasonEntity = 0L;
#endif
                for (var ri = 0; ri < info.Length; ri++)
                {
                    var type = (Settings.DynamicConcealType) ri;
                    var distSq = info[ri];
                    var minDist = Config.Config.GetValueOrDefault(type, 0);
                    if (distSq < minDist)
                    {
                        concealed = false;
                        reason = type;
#if DEBUG_DYNAMIC_CONCEAL_OWNERSHIP
                        distanceFound = Math.Sqrt(distSq);
                        distanceConfig = Math.Sqrt(minDist);
                        reasonOwner = ownerInfo[2 * ri];
                        reasonEntity = ownerInfo[2 * ri + 1];
#endif
                        break;
                    }
                }
#if DEBUG_DYNAMIC_CONCEAL_OWNERSHIP
                if (concealed != ConcealState)
                {
                    if (concealed)
                        _log.Debug(
                            $"Concealing {block.CubeGrid.DisplayName} -> {(block as MyTerminalBlock)?.CustomName} -> {block.EntityId}");
                    else
                        _log.Debug(
                            $"Revealing {block.CubeGrid.DisplayName} -> {(block as MyTerminalBlock)?.CustomName} -> {block.EntityId}  ({distanceFound} < {distanceConfig})    b/c {reason}.  "
                            + $"Cause/Owner was {MySession.Static.Players.TryGetIdentity(reasonOwner)?.DisplayName ?? $"ID{reasonOwner}"}. "
                            + $"Cause/Entity was {MyEntities.GetEntityById(reasonEntity)?.DisplayName ?? "unknown"}");
                }
#endif
                ConcealState = concealed;
                LastConcealStateUpdate = DateTime.Now;
            }

            public WorkOptions Options { get; } = new WorkOptions() {MaximumThreads = 1};

            public void ScheduleRefresh()
            {
                lock (this)
                {
                    if (_task.IsComplete)
                        _task = ParallelTasks.Parallel.Start(this);
                }
            }

            public WorkPriority Priority => WorkPriority.Low;
        }

        private class DynamicConcealmentTargetInfo
        {
            /// <summary>
            /// Distances are squared
            /// </summary>
            public Dictionary<Settings.DynamicConcealType, double> Config { get; } =
                new Dictionary<Settings.DynamicConcealType, double>();

            /// <summary>
            /// Distance is _not_ squared
            /// </summary>
            public double MaxDistance;

            internal void Merge(Settings.DynamicConcealSettings s)
            {
                var d = s.Distance * s.Distance;
                if (!Config.TryGetValue(s.DynamicConcealType, out double cval) || cval < d)
                    Config[s.DynamicConcealType] = d;
                MaxDistance = Math.Max(MaxDistance, s.Distance);
            }

            public override string ToString()
            {
                return $"DynConceal[{string.Join(", ", Config.Select(x => x.Key + "=" + x.Value))}]";
            }
        }

        private void RebuildFromSettings(ICollection<Settings.DynamicConcealSettings> config)
        {
            using (_configLock.WriteUsing())
            {
                _genericConfig.Clear();
                _config.Clear();
                foreach (var setting in config)
                {
                    if (setting.Distance <= 0 || !setting.TargetTypeId.HasValue)
                    {
                        _log.Warn(
                            $"Ignore setting {setting.TargetTypeIdString}/{setting.TargetSubtypeId} {setting.DynamicConcealType} {setting.Distance}");
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(setting.TargetSubtypeId)) continue;
                    if (!_genericConfig.TryGetValue(setting.TargetTypeId.Value, out DynamicConcealmentTargetInfo val))
                        val = _genericConfig[setting.TargetTypeId.Value] = new DynamicConcealmentTargetInfo();
                    val.Merge(setting);
                    _log.Debug(
                        $"Register setting {setting.TargetTypeIdString}/{setting.TargetSubtypeId} {setting.DynamicConcealType} {setting.Distance}");
                }
                foreach (var setting in config)
                {
                    if (setting.Distance <= 0 || !setting.TargetTypeId.HasValue)
                    {
                        _log.Warn(
                            $"Ignore setting {setting.TargetTypeIdString}/{setting.TargetSubtypeId} {setting.DynamicConcealType} {setting.Distance}");
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(setting.TargetSubtypeId)) continue;
                    var id = new MyDefinitionId(setting.TargetTypeId.Value, setting.TargetSubtypeId);
                    if (!_config.TryGetValue(id, out DynamicConcealmentTargetInfo val))
                        val = _config[id] = new DynamicConcealmentTargetInfo();
                    val.Merge(setting);
                    _log.Debug(
                        $"Register setting {setting.TargetTypeIdString}/{setting.TargetSubtypeId} {setting.DynamicConcealType} {setting.Distance}");
                }
                foreach (var nv in _config)
                {
                    var baseConfig = _genericConfig.GetValueOrDefault(nv.Key.TypeId);
                    if (baseConfig != null)
                        foreach (var kv in baseConfig.Config)
                            if (!nv.Value.Config.ContainsKey(kv.Key))
                            {
                                nv.Value.Config.Add(kv.Key, kv.Value);
                                nv.Value.MaxDistance = Math.Max(nv.Value.MaxDistance, Math.Sqrt(kv.Value));
                            }
                }
                _configVersion++;
            }
        }

        private static DynamicConcealmentTargetInfo QueryConfig(MyDefinitionId id)
        {
            if (_config.TryGetValue(id, out var res))
                return res;
            var type = id.TypeId;
            while (!type.IsNull)
            {
                if (_genericConfig.TryGetValue(type, out res))
                    return res;
                type = ((Type) type).BaseType;
            }
            return null;
        }

        private static DynamicConcealmentBlockInfo GetConcealInfo(MyCubeBlock key)
        {
            var data = _blockUpdateInfo.GetValue(key, (x) => new DynamicConcealmentBlockInfo(x));
            if (data.ConfigVersion != _configVersion)
                using (_configLock.ReadUsing())
                {
                    data.ConcealState = true;
                    data.ConfigVersion = _configVersion;
                    data.Config = QueryConfig(key.BlockDefinition.Id);
                    data.LastConcealStateUpdate = DateTime.Now - _rebuildConcealState;
                }
            return data;
        }

        private static DynamicConcealmentGridInfo GetConcealInfo(MyCubeGrid key)
        {
            var data = _gridUpdateInfo.GetValue(key, (x) => new DynamicConcealmentGridInfo(x));
            if (data.ConfigVersion != _configVersion)
                using (_configLock.ReadUsing())
                {
                    data.ConfigVersion = _configVersion;
                    data.NextQueryDistance = 0;
                    data.NearbyEntities.Clear();
                }
            return data;
        }

        private static readonly ReaderWriterLockSlim _configLock =
            new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        private static int _configVersion;

        private static readonly Dictionary<MyObjectBuilderType, DynamicConcealmentTargetInfo> _genericConfig
            = new Dictionary<MyObjectBuilderType, DynamicConcealmentTargetInfo>(MyObjectBuilderType.Comparer);

        private static readonly Dictionary<MyDefinitionId, DynamicConcealmentTargetInfo> _config =
            new Dictionary<MyDefinitionId, DynamicConcealmentTargetInfo>(MyDefinitionId.Comparer);

        private static readonly ConditionalWeakTable<MyCubeBlock, DynamicConcealmentBlockInfo> _blockUpdateInfo =
            new ConditionalWeakTable<MyCubeBlock, DynamicConcealmentBlockInfo>();

        private static readonly ConditionalWeakTable<MyCubeGrid, DynamicConcealmentGridInfo> _gridUpdateInfo =
            new ConditionalWeakTable<MyCubeGrid, DynamicConcealmentGridInfo>();

        private static DateTime _lastGridRebalance;
        private static readonly HashSet<long> _gridRecastBalancing = new HashSet<long>();

        private static readonly MyConcurrentObjectsPool<List<MyEntity>> _entityListPool =
            new MyConcurrentObjectsPool<List<MyEntity>>(8);

        /// <summary>
        /// Balances the rebuild queries for each grid out over time
        /// </summary>
        /// <param name="caller"></param>
        private static void BalanceTick(MyCubeGrid caller)
        {
            lock (_gridRecastBalancing)
            {
                _gridRecastBalancing.Add(caller.EntityId);
                if (_lastGridRebalance + _rebalanceTiming > DateTime.Now)
                {
                    _gridRecastBalancing.Clear();
                    var rawRebuildInterval = _rebuildNearbyList.Ticks;
                    var rawBalancedInterval = _rebuildNearbyList.Ticks / _gridRecastBalancing.Count;
                    var i = 0;
                    foreach (var id in _gridRecastBalancing)
                    {
                        if (!MyEntities.TryGetEntityById(id, out MyEntity ent) || !(ent is MyCubeGrid grid))
                            continue;
                        var data = GetConcealInfo(grid);
                        var lastUpdateRaw = data.LastNearbyUpdate.Ticks;
                        var epoch = (lastUpdateRaw / rawRebuildInterval) + rawRebuildInterval;
                        var balancedTime = epoch + rawBalancedInterval * i;
                        if (balancedTime > lastUpdateRaw)
                            data.LastNearbyUpdate = new DateTime(balancedTime - rawRebuildInterval);
                        else
                            data.LastNearbyUpdate = new DateTime(balancedTime);
                        i++;
                    }
                    _lastGridRebalance = DateTime.Now;
                }
            }
        }

        // ReSharper disable once SuggestBaseTypeForParameter
        private static bool PrefixUpdate(MyEntity __instance)
        {
            var block = __instance as MyCubeBlock;
            if (block == null)
                return true;
            {
                if (block is MyLargeTurretBase turret && turret.IsControlledByLocalPlayer)
                    return true;
            }
            var cfg = GetConcealInfo(block);
            if (cfg?.Config == null || cfg.Config.Config.Count == 0)
                return true;

            if (cfg.LastConcealStateUpdate + _rebuildConcealState < DateTime.Now)
            {
                cfg.ScheduleRefresh();
            }

            return !cfg.ConcealState;
        }

        private static MyRelationsBetweenPlayerAndBlock GetRelationTolerant(long id1, long id2)
        {
            if (id1 == id2)
                return MyRelationsBetweenPlayerAndBlock.Owner;
            if (id1 == 0 || id2 == 0)
                return MyRelationsBetweenPlayerAndBlock.NoOwnership;
            IMyFaction playerFaction1 = MySession.Static.Factions.TryGetPlayerFaction(id1);
            IMyFaction playerFaction2 = MySession.Static.Factions.TryGetPlayerFaction(id2);
            if (playerFaction1 == null || playerFaction2 == null)
                return MyRelationsBetweenPlayerAndBlock.Enemies;
            if (playerFaction1 == playerFaction2)
                return MyRelationsBetweenPlayerAndBlock.FactionShare;
            return MySession.Static.Factions.GetRelationBetweenFactions(playerFaction1.FactionId,
                       playerFaction2.FactionId) == MyRelationsBetweenFactions.Neutral
                ? MyRelationsBetweenPlayerAndBlock.Neutral
                : MyRelationsBetweenPlayerAndBlock.Enemies;
        }

        private static Settings.DynamicConcealType GetConcealType(MyRelationsBetweenPlayerAndBlock relation, bool grid)
        {
            switch (relation)
            {
                case MyRelationsBetweenPlayerAndBlock.Owner:
                case MyRelationsBetweenPlayerAndBlock.FactionShare:
                    return grid ? Settings.DynamicConcealType.None : Settings.DynamicConcealType.FriendlyCharacters;
                case MyRelationsBetweenPlayerAndBlock.Enemies:
                    return grid
                        ? Settings.DynamicConcealType.HostileGrids
                        : Settings.DynamicConcealType.HostileCharacters;
                case MyRelationsBetweenPlayerAndBlock.Neutral:
                case MyRelationsBetweenPlayerAndBlock.NoOwnership:
                default:
                    return grid
                        ? Settings.DynamicConcealType.NeutralGrids
                        : Settings.DynamicConcealType.NeutralCharacters;
            }
        }
    }
}