using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Groups;
using VRageMath;

namespace Concealment
{
    public class ConcealGroup
    {
        /// <summary>
        /// Entity ID of the first grid in the group.
        /// </summary>
        public long Id { get; }
        public bool IsConcealed { get; set; }
        public BoundingBoxD WorldAABB { get; private set; }
        public List<MyCubeGrid> Grids { get; }
        public List<MyMedicalRoom> MedicalRooms { get; } = new List<MyMedicalRoom>();
        public List<MyCryoChamber> CryoChambers { get; } = new List<MyCryoChamber>();
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

        private void Grid_OnMarkForClose(VRage.Game.Entity.MyEntity obj)
        {
            LogManager.GetLogger(nameof(ConcealGroup)).Info($"Grid group '{GridNames}' was marked for close.");
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
    }

}
