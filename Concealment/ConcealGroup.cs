using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Audio;
using VRage.Game.Entity;
using VRage.Game.Entity.EntityComponents.Interfaces;
using VRage.Groups;
using VRage.ModAPI;
using VRageMath;

namespace Concealment
{
    public class ConcealGroup
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();
        /// <summary>
        /// Entity ID of the first grid in the group.
        /// </summary>
        public long Id { get; }
        public bool IsConcealed { get; private set; }
        public BoundingBoxD WorldAABB { get; private set; }
        public List<MyCubeGrid> Grids { get; }
        public List<MyMedicalRoom> MedicalRooms { get; } = new List<MyMedicalRoom>();
        public List<MyCryoChamber> CryoChambers { get; } = new List<MyCryoChamber>();
        //private Dictionary<long, bool> _unstatic = new Dictionary<long, bool>();
        public event Action<ConcealGroup> Closing;
        internal volatile int ProxyId = -1;

        public ConcealGroup(MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group group)
        {
            Grids = group.Nodes.Select(n => n.NodeData).ToList();
            Id = Grids.First().EntityId;
        }

        public string GridNames
        {
            get { return string.Join(", ", Grids.Select(g => g.DisplayName)); }
            set { }
        }

        public void UpdatePostConceal()
        {
            IsConcealed = true;
            UpdateAABB();
            CacheSpawns();
            HookOnClosing();
        }

        public void UpdatePostReveal()
        {
            IsConcealed = false;
            UnhookOnClosing();
        }

        private void HookOnClosing()
        {
            foreach (var grid in Grids)
                grid.OnMarkForClose += Grid_OnMarkForClose;
        }

        private void UnhookOnClosing()
        {
            foreach (var grid in Grids)
                grid.OnMarkForClose -= Grid_OnMarkForClose;
        }

        private void Grid_OnMarkForClose(MyEntity obj)
        {
            _log.Debug($"Grid group '{GridNames}' was marked for close.");
            EnableProjectors();
            UnhookOnClosing();
            Closing?.Invoke(this);
        }

        public void UpdateAABB()
        {
            var startPos = Grids.First().PositionComp.GetPosition();
            var box = new BoundingBoxD(startPos, startPos);

            foreach (var aabb in Grids.Select(g => g.PositionComp.WorldAABB))
                box.Include(aabb);

            WorldAABB = box;
        }

        private void CacheSpawns()
        {
            MedicalRooms.Clear();
            CryoChambers.Clear();

            foreach (var block in Grids.SelectMany(x => x.GetFatBlocks()))
            {
                if (block is MyMedicalRoom medical)
                    MedicalRooms.Add(medical);
                else if (block is MyCryoChamber cryo)
                    CryoChambers.Add(cryo);
            }
        }

        public bool IsMedicalRoomAvailable(long identityId)
        {
            foreach (var room in MedicalRooms)
            {
                if (room.HasPlayerAccess(identityId) && room.IsWorking)
                    return true;
            }

            return false;
        }

        public bool IsCryoOccupied(ulong steamId)
        {
            var currentIdField = typeof(MyCryoChamber).GetField("m_currentPlayerId", BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var chamber in CryoChambers)
            {
                var value = (MyPlayer.PlayerId?)currentIdField.GetValue(chamber);
                if (value?.SteamId == steamId)
                    return true;
            }

            return false;
        }

        private readonly HashSet<long> _projectors = new HashSet<long>();
        private void DisableProjectors(MyCubeGrid grid)
        {
            foreach (var projector in grid.GetFatBlocks<MyProjectorBase>())
            {
                if (projector.ProjectedGrid == null)
                    continue;

                projector.Enabled = false;
                _projectors.Add(projector.EntityId);
            }
        }

        public void EnableProjectors()
        {
            foreach (var projector in _projectors.Select(x => (MyProjectorBase)MyEntities.GetEntityById(x)))
            {
                projector.Enabled = true;
            }
            _projectors.Clear();
        }

        /// <summary>
        /// Conceals this group from game and physics logic.
        /// </summary>
        public void Conceal()
        {
            foreach (var grid in Grids)
            {
                DisableProjectors(grid);
                
                if (grid.Parent == null)
                    UnregisterRecursive(grid);   
            }

            void UnregisterRecursive(MyEntity e)
            {
                if (e.IsPreview)
                    return;
                
                MyEntities.UnregisterForUpdate(e);
                (e.GameLogic as IMyGameLogicComponent)?.UnregisterForUpdate();
                e.Flags |= (EntityFlags)4;
                if (e.Hierarchy == null)
                    return;

                foreach (var child in e.Hierarchy.Children)
                    UnregisterRecursive((MyEntity)child.Container.Entity);
            }
        }

        /// <summary>
        /// Reveals this group to game and physics logic.
        /// </summary>
        public void Reveal()
        {
            foreach (var grid in Grids)
            {
                if (grid.Parent == null)
                    RegisterRecursive(grid);   
            }
            
            EnableProjectors();

            void RegisterRecursive(MyEntity e)
            {
                if (e.IsPreview)
                    return;
                
                MyEntities.RegisterForUpdate(e);
                (e.GameLogic as IMyGameLogicComponent)?.RegisterForUpdate();
                e.Flags &= ~(EntityFlags)4;
                if (e.Hierarchy == null)
                    return;

                foreach (var child in e.Hierarchy.Children)
                    RegisterRecursive((MyEntity)child.Container.Entity);
            }
        }
    }

}
