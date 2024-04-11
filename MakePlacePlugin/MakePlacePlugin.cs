﻿using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Network;
using Dalamud.IoC;
using Dalamud.Logging;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Housing;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ImGuiScene;
using Lumina.Excel.GeneratedSheets;
using MakePlacePlugin.Objects;
using MakePlacePlugin.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using static MakePlacePlugin.Memory;
using HousingFurniture = Lumina.Excel.GeneratedSheets.HousingFurniture;

namespace MakePlacePlugin
{

    public class MakePlacePlugin : IDalamudPlugin
    {
        public string Name => "MakePlace Plugin";
        public PluginUi Gui { get; private set; }
        public Configuration Config { get; private set; }

        public static List<HousingItem> ItemsToPlace = new List<HousingItem>();

        private delegate bool UpdateLayoutDelegate(IntPtr a1);
        private HookWrapper<UpdateLayoutDelegate> IsSaveLayoutHook;


        // Function for selecting an item, usually used when clicking on one in game.        
        public delegate void SelectItemDelegate(IntPtr housingStruct, IntPtr item);
        private static HookWrapper<SelectItemDelegate> SelectItemHook;

        public static bool CurrentlyPlacingItems = false;

        public static bool ApplyChange = false;

        public static SaveLayoutManager LayoutManager;

        public static bool logHousingDetour = false;

        internal static Location PlotLocation = new Location();

        public Layout Layout = new Layout();
        public List<HousingItem> InteriorItemList = new List<HousingItem>();
        public List<HousingItem> ExteriorItemList = new List<HousingItem>();
        public List<HousingItem> UnusedItemList = new List<HousingItem>();

        public void Dispose()
        {

            HookManager.Dispose();

            DalamudApi.ClientState.TerritoryChanged -= TerritoryChanged;
            DalamudApi.CommandManager.RemoveHandler("/makeplace");
            Gui?.Dispose();

        }

        public MakePlacePlugin(DalamudPluginInterface pi)
        {
            DalamudApi.Initialize(pi);

            Config = DalamudApi.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Config.Save();

            Initialize();

            DalamudApi.CommandManager.AddHandler("/makeplace", new CommandInfo(CommandHandler)
            {
                HelpMessage = "load config window."
            });
            Gui = new PluginUi(this);
            DalamudApi.ClientState.TerritoryChanged += TerritoryChanged;


            HousingData.Init(this);
            Memory.Init();
            LayoutManager = new SaveLayoutManager(this, Config);

            DalamudApi.PluginLog.Info("MakePlace Plugin v3.4.3 initialized");
        }
        public void Initialize()
        {

            IsSaveLayoutHook = HookManager.Hook<UpdateLayoutDelegate>("40 53 48 83 ec 20 48 8b d9 48 8b 0d ?? ?? ?? ?? e8 ?? ?? ?? ?? 33 d2 48 8b c8 e8 ?? ?? ?? ?? 84 c0 75 7d 38 83 ?? 01 00 00", IsSaveLayoutDetour);

            SelectItemHook = HookManager.Hook<SelectItemDelegate>("E8 ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 48 8B 6C 24 40 48 8B CE", SelectItemDetour);

            UpdateYardObjHook = HookManager.Hook<UpdateYardDelegate>("48 89 74 24 18 57 48 83 ec 20 b8 dc 02 00 00 0f b7 f2 48 8b f9 66 3b d0 0f", UpdateYardObj);

            GetGameObjectHook = HookManager.Hook<GetObjectDelegate>("48 89 5c 24 08 48 89 74 24 10 57 48 83 ec 20 0f b7 f2 33 db 0f 1f 40 00 0f 1f 84 00 00 00 00 00", GetGameObject);

            GetObjectFromIndexHook = HookManager.Hook<GetActiveObjectDelegate>("81 fa 90 01 00 00 75 08 48 8b 81 88 0c 00 00 c3 0f b7 81 90 0c 00 00 3b d0 72 03 33 c0 c3", GetObjectFromIndex);

            GetYardIndexHook = HookManager.Hook<GetIndexDelegate>("48 89 6c 24 18 56 48 83 ec 20 0f b6 ea 0f b6 f1 84 c9 79 22 0f b6 c1", GetYardIndex);

        }

        internal delegate ushort GetIndexDelegate(byte type, byte objStruct);
        internal static HookWrapper<GetIndexDelegate> GetYardIndexHook;
        internal static ushort GetYardIndex(byte plotNumber, byte inventoryIndex)
        {
            var result = GetYardIndexHook.Original(plotNumber, inventoryIndex);
            return result;
        }

        internal delegate IntPtr GetActiveObjectDelegate(IntPtr ObjList, uint index);

        internal static IntPtr GetObjectFromIndex(IntPtr ObjList, uint index)
        {
            var result = GetObjectFromIndexHook.Original(ObjList, index);
            return result;
        }

        internal delegate IntPtr GetObjectDelegate(IntPtr ObjList, ushort index);
        internal static HookWrapper<GetObjectDelegate> GetGameObjectHook;
        internal static HookWrapper<GetActiveObjectDelegate> GetObjectFromIndexHook;

        internal static IntPtr GetGameObject(IntPtr ObjList, ushort index)
        {
            return GetGameObjectHook.Original(ObjList, index);
        }

        public delegate void UpdateYardDelegate(IntPtr housingStruct, ushort index);
        private static HookWrapper<UpdateYardDelegate> UpdateYardObjHook;


        private void UpdateYardObj(IntPtr objectList, ushort index)
        {
            UpdateYardObjHook.Original(objectList, index);
        }

        unsafe static public void SelectItemDetour(IntPtr housing, IntPtr item)
        {
            SelectItemHook.Original(housing, item);
        }


        unsafe static public void SelectItem(IntPtr item)
        {
            SelectItemDetour((IntPtr)Memory.Instance.HousingStructure, item);
        }


        public unsafe void PlaceItems()
        {

            if (!Memory.Instance.CanEditItem() || ItemsToPlace.Count == 0)
            {
                return;
            }

            try
            {

                if (Memory.Instance.GetCurrentTerritory() == Memory.HousingArea.Outdoors)
                {
                    GetPlotLocation();
                }

                while (ItemsToPlace.Count > 0)
                {
                    var item = ItemsToPlace.First();
                    ItemsToPlace.RemoveAt(0);

                    if (item.ItemStruct == IntPtr.Zero) continue;

                    if (item.CorrectLocation && item.CorrectRotation)
                    {
                        Log($"{item.Name} is already correctly placed");
                        continue;
                    }

                    SetItemPosition(item);

                    if (Config.LoadInterval > 0)
                    {
                        Thread.Sleep(Config.LoadInterval);
                    }

                }

                if (ItemsToPlace.Count == 0)
                {
                    Log("Finished applying layout");
                }

            }
            catch (Exception e)
            {
                LogError($"Error: {e.Message}", e.StackTrace);
            }

            CurrentlyPlacingItems = false;
        }

        unsafe public static void SetItemPosition(HousingItem rowItem)
        {

            if (!Memory.Instance.CanEditItem())
            {
                LogError("Unable to set position outside of Rotate Layout mode");
                return;
            }

            if (rowItem.ItemStruct == IntPtr.Zero) return;

            Log("Placing " + rowItem.Name);

            var MemInstance = Memory.Instance;

            logHousingDetour = true;
            ApplyChange = true;

            SelectItem(rowItem.ItemStruct);


            Vector3 position = new Vector3(rowItem.X, rowItem.Y, rowItem.Z);
            Vector3 rotation = new Vector3();

            rotation.Y = (float)(rowItem.Rotate * 180 / Math.PI);

            if (MemInstance.GetCurrentTerritory() == Memory.HousingArea.Outdoors)
            {
                var rotateVector = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -PlotLocation.rotation);
                position = Vector3.Transform(position, rotateVector) + PlotLocation.ToVector();
                rotation.Y = (float)((rowItem.Rotate - PlotLocation.rotation) * 180 / Math.PI);
            }
            MemInstance.WritePosition(position);
            MemInstance.WriteRotation(rotation);

            rowItem.CorrectLocation = true;
            rowItem.CorrectRotation = true;

        }

        public void ApplyLayout()
        {
            if (CurrentlyPlacingItems)
            {
                Log($"Already placing items");
                return;
            }

            CurrentlyPlacingItems = true;
            Log($"Applying layout with interval of {Config.LoadInterval}ms");

            ItemsToPlace.Clear();

            List<HousingItem> placedLast = new List<HousingItem>();

            List<HousingItem> toBePlaced;

            if (Memory.Instance.GetCurrentTerritory() == Memory.HousingArea.Indoors)
            {
                toBePlaced = new List<HousingItem>();
                foreach (var houseItem in InteriorItemList)
                {
                    if (IsSelectedFloor(houseItem.Y))
                    {
                        toBePlaced.Add(houseItem);
                    }
                }
            }
            else
            {
                toBePlaced = new List<HousingItem>(ExteriorItemList);
            }

            foreach (var item in toBePlaced)
            {
                if (item.IsTableOrWallMounted)
                {
                    placedLast.Add(item);
                }
                else
                {
                    ItemsToPlace.Add(item);
                }
            }

            ItemsToPlace.AddRange(placedLast);


            var thread = new Thread(PlaceItems);
            thread.Start();
        }

        public bool MatchItem(HousingItem item, uint itemKey)
        {
            if (item.ItemStruct != IntPtr.Zero) return false;       // this item is already matched. We can skip

            return item.ItemKey == itemKey && IsSelectedFloor(item.Y);
        }

        public unsafe bool MatchExactItem(HousingItem item, uint itemKey, HousingGameObject obj)
        {
            if (!MatchItem(item, itemKey)) return false;

            if (item.Stain != obj.color) return false;

            var matNumber = obj.Item->MaterialManager->MaterialSlot1;

            if (item.MaterialItemKey == 0 && matNumber == 0) return true;
            else if (item.MaterialItemKey != 0 && matNumber == 0) return false;

            var matItemKey = HousingData.Instance.GetMaterialItemKey(item.ItemKey, matNumber);
            if (matItemKey == 0) return true;

            return matItemKey == item.MaterialItemKey;

        }

        public unsafe void MatchLayout()
        {

            List<HousingGameObject> allObjects = null;
            Memory Mem = Memory.Instance;

            Quaternion rotateVector = new();
            var currentTerritory = Mem.GetCurrentTerritory();

            switch (currentTerritory)
            {
                case HousingArea.Indoors:
                    Mem.TryGetNameSortedHousingGameObjectList(out allObjects);
                    InteriorItemList.ForEach(item =>
                    {
                        item.ItemStruct = IntPtr.Zero;
                    });
                    break;

                case HousingArea.Outdoors:
                    GetPlotLocation();
                    allObjects = Mem.GetExteriorPlacedObjects();
                    ExteriorItemList.ForEach(item =>
                    {
                        item.ItemStruct = IntPtr.Zero;
                    });
                    rotateVector = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlotLocation.rotation);
                    break;
                case HousingArea.Island:
                    Mem.TryGetIslandGameObjectList(out allObjects);
                    ExteriorItemList.ForEach(item =>
                    {
                        item.ItemStruct = IntPtr.Zero;
                    });
                    break;
            }

            List<HousingGameObject> unmatched = new List<HousingGameObject>();

            // first we find perfect match
            foreach (var gameObject in allObjects)
            {
                if (!IsSelectedFloor(gameObject.Y)) continue;

                uint furnitureKey = gameObject.housingRowId;
                HousingItem houseItem = null;

                Vector3 localPosition = new Vector3(gameObject.X, gameObject.Y, gameObject.Z);
                float localRotation = gameObject.rotation;

                if (currentTerritory == HousingArea.Indoors)
                {
                    var furniture = DalamudApi.DataManager.GetExcelSheet<HousingFurniture>().GetRow(furnitureKey);
                    var itemKey = furniture.Item.Value.RowId;
                    houseItem = Utils.GetNearestHousingItem(
                        InteriorItemList.Where(item => MatchExactItem(item, itemKey, gameObject)),
                        localPosition
                    );
                }
                else
                {
                    if (currentTerritory == HousingArea.Outdoors)
                    {
                        localPosition = Vector3.Transform(localPosition - PlotLocation.ToVector(), rotateVector);
                        localRotation += PlotLocation.rotation;
                    }
                    var furniture = DalamudApi.DataManager.GetExcelSheet<HousingYardObject>().GetRow(furnitureKey);
                    var itemKey = furniture.Item.Value.RowId;
                    houseItem = Utils.GetNearestHousingItem(
                        ExteriorItemList.Where(item => MatchExactItem(item, itemKey, gameObject)),
                        localPosition
                    );

                }

                if (houseItem == null)
                {
                    unmatched.Add(gameObject);
                    continue;
                }

                // check if it's already correctly placed & rotated
                var locationError = houseItem.GetLocation() - localPosition;
                houseItem.CorrectLocation = locationError.LengthSquared() < 0.00001;
                houseItem.CorrectRotation = localRotation - houseItem.Rotate < 0.001;

                houseItem.ItemStruct = (IntPtr)gameObject.Item;
            }

            UnusedItemList.Clear();

            // then we match even if the dye doesn't fit
            foreach (var gameObject in unmatched)
            {

                uint furnitureKey = gameObject.housingRowId;
                HousingItem houseItem = null;

                Item item;
                Vector3 localPosition = new Vector3(gameObject.X, gameObject.Y, gameObject.Z);
                float localRotation = gameObject.rotation;

                if (currentTerritory == HousingArea.Indoors)
                {
                    var furniture = DalamudApi.DataManager.GetExcelSheet<HousingFurniture>().GetRow(furnitureKey);
                    item = furniture.Item.Value;
                    houseItem = Utils.GetNearestHousingItem(
                        InteriorItemList.Where(hItem => MatchItem(hItem, item.RowId)),
                        new Vector3(gameObject.X, gameObject.Y, gameObject.Z)
                    );
                }
                else
                {
                    if (currentTerritory == HousingArea.Outdoors)
                    {
                        localPosition = Vector3.Transform(localPosition - PlotLocation.ToVector(), rotateVector);
                        localRotation += PlotLocation.rotation;
                    }
                    var furniture = DalamudApi.DataManager.GetExcelSheet<HousingYardObject>().GetRow(furnitureKey);
                    item = furniture.Item.Value;
                    houseItem = Utils.GetNearestHousingItem(
                        ExteriorItemList.Where(hItem => MatchItem(hItem, item.RowId)),
                        localPosition
                    );
                }
                if (houseItem == null)
                {
                    var unmatchedItem = new HousingItem(
                    item,
                    gameObject.color,
                    gameObject.X,
                    gameObject.Y,
                    gameObject.Z,
                    gameObject.rotation);
                    UnusedItemList.Add(unmatchedItem);
                    continue;
                }

                // check if it's already correctly placed & rotated
                var locationError = houseItem.GetLocation() - localPosition;
                houseItem.CorrectLocation = locationError.LengthSquared() < 0.0001;
                houseItem.CorrectRotation = localRotation - houseItem.Rotate < 0.001;

                houseItem.DyeMatch = false;

                houseItem.ItemStruct = (IntPtr)gameObject.Item;

            }

        }

        public unsafe void GetPlotLocation()
        {
            var mgr = Memory.Instance.HousingModule->outdoorTerritory;
            var territoryId = Memory.Instance.GetTerritoryTypeId();
            var row = DalamudApi.DataManager.GetExcelSheet<TerritoryType>().GetRow(territoryId);

            if (row == null)
            {
                LogError("Cannot identify territory");
                return;
            }

            var placeName = row.Name.ToString();

            PlotLocation = Plots.Map[placeName][mgr->Plot + 1];
        }


        public unsafe void LoadExterior()
        {

            SaveLayoutManager.LoadExteriorFixtures();

            ExteriorItemList.Clear();

            var mgr = Memory.Instance.HousingModule->outdoorTerritory;

            var outdoorMgrAddr = (IntPtr)mgr;
            var objectListAddr = outdoorMgrAddr + 0x10;
            var activeObjList = objectListAddr + 0x8968;

            var exteriorItems = Memory.GetContainer(InventoryType.HousingExteriorPlacedItems);

            GetPlotLocation();

            var rotateVector = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlotLocation.rotation);

            switch (PlotLocation.size)
            {
                case "s":
                    Layout.houseSize = "Small";
                    break;
                case "m":
                    Layout.houseSize = "Medium";
                    break;
                case "l":
                    Layout.houseSize = "Large";
                    break;

            }

            Layout.exteriorScale = 1;
            Layout.properties["entranceLayout"] = PlotLocation.entranceLayout;

            for (int i = 0; i < exteriorItems->Size; i++)
            {
                var item = exteriorItems->GetInventorySlot(i);
                if (item == null || item->ItemID == 0) continue;

                var itemRow = DalamudApi.DataManager.GetExcelSheet<Item>().GetRow(item->ItemID);
                if (itemRow == null) continue;

                var itemInfoIndex = GetYardIndex(mgr->Plot, (byte)i);

                var itemInfo = HousingObjectManager.GetItemInfo(mgr, itemInfoIndex);
                if (itemInfo == null)
                {
                    continue;
                }

                var location = new Vector3(itemInfo->X, itemInfo->Y, itemInfo->Z);

                var newLocation = Vector3.Transform(location - PlotLocation.ToVector(), rotateVector);

                var housingItem = new HousingItem(
                    itemRow,
                    item->Stain,
                    newLocation.X,
                    newLocation.Y,
                    newLocation.Z,
                    itemInfo->Rotation + PlotLocation.rotation
                );


                var gameObj = (HousingGameObject*)GetObjectFromIndex(activeObjList, itemInfo->ObjectIndex);

                if (gameObj == null)
                {
                    gameObj = (HousingGameObject*)GetGameObject(objectListAddr, itemInfoIndex);

                    if (gameObj != null)
                    {

                        location = new Vector3(gameObj->X, gameObj->Y, gameObj->Z);

                        newLocation = Vector3.Transform(location - PlotLocation.ToVector(), rotateVector);


                        housingItem.X = newLocation.X;
                        housingItem.Y = newLocation.Y;
                        housingItem.Z = newLocation.Z;
                    }
                }

                if (gameObj != null)
                {
                    housingItem.ItemStruct = (IntPtr)gameObj->Item;
                }

                ExteriorItemList.Add(housingItem);
            }

            Config.Save();
        }

        public bool IsSelectedFloor(float y)
        {
            if (Memory.Instance.GetCurrentTerritory() != Memory.HousingArea.Indoors || Memory.Instance.GetIndoorHouseSize().Equals("Apartment")) return true;

            if (y < -0.001) return Config.Basement;
            if (y >= -0.001 && y < 6.999) return Config.GroundFloor;

            if (y >= 6.999)
            {
                if (Memory.Instance.HasUpperFloor()) return Config.UpperFloor;
                else return Config.GroundFloor;
            }

            return false;
        }


        public unsafe void LoadInterior()
        {
            SaveLayoutManager.LoadInteriorFixtures();

            List<HousingGameObject> dObjects;
            Memory.Instance.TryGetNameSortedHousingGameObjectList(out dObjects);

            InteriorItemList.Clear();

            foreach (var gameObject in dObjects)
            {
                uint furnitureKey = gameObject.housingRowId;

                var furniture = DalamudApi.DataManager.GetExcelSheet<HousingFurniture>().GetRow(furnitureKey);
                Item item = furniture?.Item?.Value;

                if (item == null) continue;
                if (item.RowId == 0) continue;

                if (!IsSelectedFloor(gameObject.Y)) continue;

                var housingItem = new HousingItem(item, gameObject);
                housingItem.ItemStruct = (IntPtr)gameObject.Item;

                if (gameObject.Item != null && gameObject.Item->MaterialManager != null)
                {
                    ushort material = gameObject.Item->MaterialManager->MaterialSlot1;
                    housingItem.MaterialItemKey = HousingData.Instance.GetMaterialItemKey(item.RowId, material);
                }

                InteriorItemList.Add(housingItem);
            }

            Config.Save();

        }


        public unsafe void LoadIsland()
        {
            SaveLayoutManager.LoadIslandFixtures();

            List<HousingGameObject> objects;
            Memory.Instance.TryGetIslandGameObjectList(out objects);
            ExteriorItemList.Clear();

            foreach (var gameObject in objects)
            {
                uint furnitureKey = gameObject.housingRowId;
                var furniture = DalamudApi.DataManager.GetExcelSheet<HousingYardObject>().GetRow(furnitureKey);
                Item item = furniture?.Item?.Value;

                if (item == null) continue;
                if (item.RowId == 0) continue;

                var housingItem = new HousingItem(item, gameObject);
                housingItem.ItemStruct = (IntPtr)gameObject.Item;

                ExteriorItemList.Add(housingItem);
            }

            Config.Save();
        }

        public void GetGameLayout()
        {

            Memory Mem = Memory.Instance;
            var currentTerritory = Mem.GetCurrentTerritory();

            var itemList = currentTerritory == HousingArea.Indoors ? InteriorItemList : ExteriorItemList;
            itemList.Clear();

            switch (currentTerritory)
            {
                case HousingArea.Outdoors:
                    LoadExterior();
                    break;

                case HousingArea.Indoors:
                    LoadInterior();
                    break;

                case HousingArea.Island:
                    LoadIsland();
                    break;
            }

            Log(String.Format("Loaded {0} furniture items", itemList.Count));

            Config.HiddenScreenItemHistory = new List<int>();
            Config.Save();
        }


        public bool IsSaveLayoutDetour(IntPtr housingStruct)
        {
            var result = IsSaveLayoutHook.Original(housingStruct);

            if (ApplyChange)
            {
                ApplyChange = false;
                return true;
            }

            return result;
        }


        private void TerritoryChanged(ushort e)
        {
            Config.DrawScreen = false;
            Config.Save();
        }

        public unsafe void CommandHandler(string command, string arguments)
        {
            var args = arguments.Trim().Replace("\"", string.Empty);

            try
            {
                if (string.IsNullOrEmpty(args) || args.Equals("config", StringComparison.OrdinalIgnoreCase))
                {
                    Gui.ConfigWindow.Visible = !Gui.ConfigWindow.Visible;
                }
            }
            catch (Exception e)
            {
                LogError(e.Message, e.StackTrace);
            }
        }

        public static void Log(string message, string detail_message = "")
        {
            var msg = $"{message}";
            DalamudApi.PluginLog.Info(detail_message == "" ? msg : detail_message);
            DalamudApi.ChatGui.Print(msg);
        }
        public static void LogError(string message, string detail_message = "")
        {
            var msg = $"{message}";
            DalamudApi.PluginLog.Error(msg);

            if (detail_message.Length > 0) DalamudApi.PluginLog.Error(detail_message);

            DalamudApi.ChatGui.PrintError(msg);
        }

    }

}
