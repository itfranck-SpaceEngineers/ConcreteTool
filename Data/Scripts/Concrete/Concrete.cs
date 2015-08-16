﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Sandbox.Common;
using Sandbox.Common.Components;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.VRageData;
using Sandbox.Definitions;
using Sandbox.Engine;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Multiplayer;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Common.Utils;
using VRageMath;
using VRage;
using VRage.ObjectBuilders;
using VRage.Voxels;
using VRage.ModAPI;
using Digi.Utils;

namespace Digi.Concrete
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Concrete : MySessionComponentBase
    {
        public static bool init { get; private set; }
        public static bool isServer { get; private set; }
        public static bool isDedicated { get; private set; }
        
        private int skipPackets = 0;
        private int skipClean = 0;
        private int retries = 0;
        private bool holdingTool = false;
        private IMyEntity cursor = null;
        private IMyHudNotification toolStatus = null;
        
        private MyVoxelMaterialDefinition material = null;
        private MyStorageDataCache cache = new MyStorageDataCache();
        private HashSet<IMyEntity> ents = new HashSet<IMyEntity>();
        private Queue<string> voxelPackets = new Queue<string>();
        
        public const byte VOXEL_OVERWRITE = 159;
        public const ushort PACKET_VOXELS = 63311;
        public static readonly Encoding encode = Encoding.Unicode;
        
        public const string CONCRETE_MATERIAL = "Concrete";
        public const string CONCRETE_TOOL = "ConcreteTool";
        public const string CONCRETE_AMMO_NAME = "Concrete Mix";
        public const string CONCRETE_AMMO_ID = "ConcreteMix";
        public const string CONCRETE_AMMO_TYPEID = "MyObjectBuilder_AmmoMagazine/ConcreteMix";
        private static MyObjectBuilder_AmmoMagazine CONCRETE_MAG = new MyObjectBuilder_AmmoMagazine() { SubtypeName = CONCRETE_AMMO_ID, ProjectilesCount = 1 };
        
        private const int SKIP_TICKS_CLEAN = 30;
        private const int SKIP_TICKS_PACKETS = 60;
        
        private const long DELAY_SHOOT = (TimeSpan.TicksPerMillisecond * 100);
        
        private static readonly Vector4 BOX_COLOR = new Vector4(0.0f, 0.0f, 1.0f, 0.05f);
        private static readonly Vector3 MISSILE_SIZE = new Vector3(0.001, 0.001, 0.001);
        
        public void Init()
        {
            Log.Info("Initialized.");
            init = true;
            isServer = MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE || MyAPIGateway.Multiplayer.IsServer;
            isDedicated = (MyAPIGateway.Utilities.IsDedicated && isServer);
            
            cache.Resize(Vector3I.One);
            
            MyAPIGateway.Multiplayer.RegisterMessageHandler(PACKET_VOXELS, ReceivedVoxels);
            
            if(material == null && !MyDefinitionManager.Static.TryGetVoxelMaterialDefinition(CONCRETE_MATERIAL, out material))
            {
                throw new Exception("ERROR: Could not get the '"+CONCRETE_MATERIAL+"' voxel material!");
            }
        }
        
        protected override void UnloadData()
        {
            init = false;
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(PACKET_VOXELS, ReceivedVoxels);
        }
        
        public void ReceivedVoxels(byte[] bytes)
        {
            string receivedString = encode.GetString(bytes);
            
            Log.Info("DEBUG: ReceivedVoxel(); received:");
            Log.Info(receivedString);
            Log.Info(" ");
            
            string[] lines = receivedString.Split('\n');
            string[] data;
            int x;
            int y;
            int z;
            
            foreach(string line in lines)
            {
                data = line.Split(new char[] { ';' }, 4);
                
                if(data.Length != 4 || !int.TryParse(data[0], out x) || !int.TryParse(data[1], out y) || !int.TryParse(data[2], out z))
                {
                    Log.Error("Invalid data: " + data.ToString());
                    continue;
                }
                
                List<IMyVoxelBase> asteroids = new List<IMyVoxelBase>();
                MyAPIGateway.Session.VoxelMaps.GetInstances(asteroids, a => a.StorageName.Equals(data[3]));
                
                if(asteroids.Count == 0)
                {
                    Log.Error("Couldn't find asteroid: " + data[3]);
                    continue;
                }
                
                SetAsteroidVoxel(asteroids[0], new Vector3I(x, y, z));
                
                Log.Info("DEBUG: " + asteroids[0].StorageName + " set voxel at "+x+","+y+","+z);
            }
        }
        
        private void SendVoxelUpdates()
        {
            if(voxelPackets.Count == 0 && skipPackets == 0)
                return;
            
            if(++skipPackets < SKIP_TICKS_PACKETS)
                return;
            
            skipPackets = 0;
            
            if(voxelPackets.Count == 0)
                return;
            
            List<string> packet = new List<string>();
            int bytes = 0;
            
            while(voxelPackets.Count > 0)
            {
                bytes += (voxelPackets.Peek().Length * sizeof(char));
                
                if(bytes < 4096)
                {
                    packet.Add(voxelPackets.Dequeue());
                }
            }
            
            string data = String.Join("\n", packet);
            
            Log.Info("DEBUG: Sending voxels; bytes="+bytes+"; data:");
            Log.Info(data);
            Log.Info("");
            
            if(MyAPIGateway.Multiplayer.SendMessageToOthers(PACKET_VOXELS, encode.GetBytes(data), true))
            {
                retries = 0;
                
                if(voxelPackets.Count > 0)
                {
                    Log.Info("DEBUG: Packet sent but there are still " + voxelPackets.Count + " packets to process!");
                }
            }
            else
            {
                if(++retries > 3)
                {
                    Log.Info("ERROR: SendMessageToOthers() failed too many times, not retrying!");
                }
                else
                {
                    Log.Info("ERROR: SendMessageToOthers() failed! Packets re-queued ("+retries+").");
                    
                    foreach(string p in packet)
                    {
                        voxelPackets.Enqueue(p);
                    }
                }
            }
        }
        
        private void QueueUpdateVoxel(string asteroidName, Vector3I pos)
        {
            voxelPackets.Enqueue(String.Format("{0};{1};{2};{3}", pos.X, pos.Y, pos.Z, asteroidName));
        }
        
        bool lastTrigger;
        long lastShotTime = 0;
        
        public override void UpdateAfterSimulation()
        {
            if(!init)
            {
                if(MyAPIGateway.Session == null)
                    return;
                
                Init();
            }
            
            SendVoxelUpdates();
            
            if(!isDedicated && MyAPIGateway.Session.Player != null && MyAPIGateway.Session.Player.Controller != null && MyAPIGateway.Session.Player.Controller.ControlledEntity != null && MyAPIGateway.Session.Player.Controller.ControlledEntity.Entity != null)
            {
                CleanHitBoxes();
                
                var player = MyAPIGateway.Session.Player.Controller.ControlledEntity.Entity;
                
                if(player is IMyCharacter)
                {
                    var character = player.GetObjectBuilder(false) as MyObjectBuilder_Character;
                    var tool = character.HandWeapon as MyObjectBuilder_AutomaticRifle;
                    
                    if(tool != null && tool.SubtypeName == CONCRETE_TOOL)
                    {
                        if(!holdingTool)
                        {
                            DrawTool();
                            lastShotTime = tool.GunBase.LastShootTime;
                        }
                        
                        bool trigger = (tool.GunBase.LastShootTime + DELAY_SHOOT) > DateTime.UtcNow.Ticks;
                        HoldingTool(lastTrigger ? false : trigger);
                        lastTrigger = trigger;
                        
                        // always add the shot ammo back
                        if(tool.GunBase.LastShootTime > lastShotTime)
                        {
                            lastShotTime = tool.GunBase.LastShootTime;
                            
                            if(!MyAPIGateway.Session.CreativeMode)
                            {
                                var inv = ((IMyInventoryOwner)player).GetInventory(0) as Sandbox.ModAPI.IMyInventory;
                                inv.AddItems((MyFixedPoint)1, CONCRETE_MAG);
                            }
                        }
                        
                        return;
                    }
                }
            }
            
            if(holdingTool)
            {
                HolsterTool();
            }
        }
        
        public void DrawTool()
        {
            holdingTool = true;
            
            if(toolStatus == null)
            {
                toolStatus = MyAPIGateway.Utilities.CreateNotification("", 500, MyFontEnum.White);
                toolStatus.Hide();
            }
        }
        
        public void HoldingTool(bool trigger)
        {
            IMyVoxelBase asteroid = GetAsteroidAt(MyAPIGateway.Session.Player.GetPosition());
            bool placed = false;
            
            if(asteroid == null)
            {
                UpdateCursorAt(null);
            }
            else
            {
                placed = ScanAndTrigger(asteroid, trigger, 4.0f);
            }
            
            if(trigger)
            {
                if(!placed)
                {
                    toolStatus.Font = MyFontEnum.Red;
                    toolStatus.Text = "Concrete can only be placed on asteroids!"; // TODO add word 'planets' when those are relevant
                    toolStatus.AliveTime = 1500;
                    toolStatus.Show();
                }
                else if(!MyAPIGateway.Session.CreativeMode)
                {
                    // expend the ammo manually
                    var player = MyAPIGateway.Session.Player.Controller.ControlledEntity.Entity;
                    var inv = ((IMyInventoryOwner)player).GetInventory(0) as Sandbox.ModAPI.IMyInventory;
                    inv.RemoveItemsOfType((MyFixedPoint)1, CONCRETE_MAG, false);
                }
            }
        }
        
        public void HolsterTool()
        {
            holdingTool = false;
            
            if(toolStatus != null)
            {
                toolStatus.Hide();
            }
            
            if(cursor != null)
            {
                cursor.Close();
                cursor = null;
            }
        }
        
        private bool ScanAndTrigger(IMyVoxelBase voxels, bool trigger, float meters, bool first = true)
        {
            Vector3D target = GetTargetAt(meters);
            Vector3I pos = AdjustTargetForAsteroid(voxels, ref target);
            UpdateCursorAt(target, voxels);
            byte voxel = GetAsteroidVoxel(voxels, pos);
            
            if(voxel == 0 || voxel < VOXEL_OVERWRITE)
            {
                if(meters == 4.0f)
                {
                    if(trigger)
                    {
                        toolStatus.Font = MyFontEnum.Red;
                        toolStatus.Text = "Aim closer to the surface.";
                        toolStatus.AliveTime = 1500;
                        toolStatus.Show();
                    }
                    
                    UpdateCursorAt(null);
                    return false;
                }
                
                if(trigger && !IsTargetBlocked(target))
                {
                    if(voxels is IMyVoxelMap)
                    {
                        SetAsteroidVoxel(voxels, pos);
                        QueueUpdateVoxel(voxels.StorageName, pos);
                        return true;
                    }
                }
            }
            else
            {
                if(meters <= 1.0f)
                {
                    if(trigger)
                    {
                        toolStatus.Font = MyFontEnum.Red;
                        toolStatus.Text = "You're too close.";
                        toolStatus.AliveTime = 1500;
                        toolStatus.Show();
                    }
                    
                    UpdateCursorAt(null);
                    return false;
                }
                
                return ScanAndTrigger(voxels, trigger, meters - 1.0f, false);
            }
            
            return false;
        }
        
        private IMyVoxelBase GetAsteroidAt(Vector3D pos)
        {
            var asteroids = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(asteroids);
            Vector3D min;
            Vector3D max;
            
            foreach(IMyVoxelBase asteroid in asteroids)
            {
                min = asteroid.PositionLeftBottomCorner;
                max = min + asteroid.Storage.Size;
                
                if(min.X <= pos.X && pos.X <= max.X && min.Y <= pos.Y && pos.Y <= max.Y && min.Z <= pos.Z && pos.Z <= max.Z)
                {
                    return asteroid;
                }
            }
            
            return null;
        }
        
        private Vector3D GetTargetAt(float meters)
        {
            var player = MyAPIGateway.Session.ControlledObject;
            var view = player.GetHeadMatrix(true, true);
            return view.Translation + (view.Forward * meters) + (Vector3D.One * 0.5);
            //return player.Entity.WorldAABB.Center + (view.Forward * meters) + (view.Up * 0.75);
        }
        
        private void UpdateCursorAt(Vector3D? target, IMyVoxelBase voxels = null)
        {
            try
            {
                if(!target.HasValue)
                {
                    if(cursor != null)
                    {
                        cursor.Close();
                        cursor = null;
                    }
                    
                    return;
                }
                
                if(cursor == null)
                {
                    cursor = SpawnPrefab();
                }
                
                if(cursor != null)
                {
                    var matrix = cursor.WorldMatrix;
                    
                    matrix.Translation = target.Value;
                    
                    cursor.SetWorldMatrix(matrix);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        private IMyEntity SpawnPrefab()
        {
            try
            {
                MyAPIGateway.Entities.RemapObjectBuilder(PrefabBuilder);
                var ent = MyAPIGateway.Entities.CreateFromObjectBuilder(PrefabBuilder);
                ent.Flags &= ~EntityFlags.Sync; // don't sync on MP
                ent.Flags &= ~EntityFlags.Save; // don't save this entity
                ent.PersistentFlags &= ~MyPersistentEntityFlags2.CastShadows;
                ent.CastShadows = false;
                
                MyAPIGateway.Entities.AddEntity(ent, true);
                return ent;
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
            
            return null;
        }
        
        private static SerializableVector3 PrefabVector0 = new SerializableVector3(0,0,0);
        private static SerializableBlockOrientation PrefabOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
        private static MyObjectBuilder_CubeGrid PrefabBuilder = new MyObjectBuilder_CubeGrid()
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Small,
            IsStatic = true,
            Skeleton = new List<BoneInfo>(),
            LinearVelocity = PrefabVector0,
            AngularVelocity = PrefabVector0,
            ConveyorLines = new List<MyObjectBuilder_ConveyorLine>(),
            BlockGroups = new List<MyObjectBuilder_BlockGroup>(),
            Handbrake = false,
            XMirroxPlane = null,
            YMirroxPlane = null,
            ZMirroxPlane = null,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            Name = "",
            DisplayName = "",
            CreatePhysics = false,
            PositionAndOrientation = new MyPositionAndOrientation(Vector3D.Zero, Vector3D.Forward, Vector3D.Up),
            CubeBlocks = new List<MyObjectBuilder_CubeBlock>()
            {
                new MyObjectBuilder_TerminalBlock()
                {
                    EntityId = 1,
                    SubtypeName = "ConcreteToolGhost",
                    Min = new SerializableVector3I(0,0,0),
                    BlockOrientation = PrefabOrientation,
                    ColorMaskHSV = PrefabVector0,
                    ShareMode = MyOwnershipShareModeEnum.None,
                    DeformationRatio = 0,
                    ShowOnHUD = false,
                }
            }
        };
        
        private Vector3I AdjustTargetForAsteroid(IMyVoxelBase asteroid, ref Vector3D target)
        {
            var pos = new Vector3I(target - asteroid.PositionLeftBottomCorner);
            target = asteroid.PositionLeftBottomCorner + new Vector3D(pos);
            return pos;
        }
        
        private void CleanHitBoxes(bool noSkip = false)
        {
            if(ents.Count > 0)
            {
                if(noSkip || ++skipClean >= SKIP_TICKS_CLEAN)
                {
                    foreach(var ent in ents)
                    {
                        MyAPIGateway.Entities.EnableEntityBoundingBoxDraw(ent, false);
                    }
                    
                    ents.Clear();
                    skipClean = 0;
                }
            }
        }
        
        private bool IsTargetBlocked(Vector3D target)
        {
            CleanHitBoxes(true);
            BoundingBoxD box = new BoundingBoxD(target - 0.5, target + 0.5);
            MyAPIGateway.Entities.GetEntities(ents, (ent => ent.Physics != null && !ent.Physics.IsStatic && (ent is IMyCubeGrid || ent is IMyFloatingObject || ent is IMyCharacter) && ent.WorldAABB.Intersects(ref box)));
            
            if(ents.Count > 0)
            {
                bool you = false;
                
                foreach(var ent in ents)
                {
                    if(!you && ent == MyAPIGateway.Session.Player.Controller.ControlledEntity.Entity)
                        you = true;
                    
                    MyAPIGateway.Entities.EnableEntityBoundingBoxDraw(ent, true, BOX_COLOR, 0.5f, null);
                }
                
                toolStatus.Font = MyFontEnum.Red;
                toolStatus.Text = (ents.Count == 1 ? (you ? "You're in the way!" : "Something is in the way!") : (you ? "You and " + (ents.Count-1) : "" + ents.Count) + " things are in the way!");
                toolStatus.AliveTime = 1500;
                toolStatus.Show();
                return true;
            }
            
            return false;
        }
        
        private byte GetAsteroidVoxel(IMyVoxelBase asteroid, Vector3I pos)
        {
            asteroid.Storage.ReadRange(cache, MyStorageDataTypeFlags.ContentAndMaterial, 0, pos, pos);
            return cache.Content(ref Vector3I.Zero);
        }
        
        private void SetAsteroidVoxel(IMyVoxelBase asteroid, Vector3I pos)
        {
            cache.Content(ref Vector3I.Zero, MyVoxelConstants.VOXEL_CONTENT_FULL);
            cache.Material(ref Vector3I.Zero, material.Index);
            asteroid.Storage.WriteRange(cache, MyStorageDataTypeFlags.ContentAndMaterial, pos, pos);
        }
    }
}