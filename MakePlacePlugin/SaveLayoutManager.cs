﻿using Dalamud.Game.Gui;
using Dalamud.Logging;
using Lumina.Excel.GeneratedSheets;
using MakePlacePlugin.Objects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Linq;
using static MakePlacePlugin.MakePlacePlugin;
using System.Drawing;
using System.Globalization;

namespace MakePlacePlugin
{

    public class Transform
    {
        public List<float> location { get; set; }
        public List<float> rotation { get; set; }
        public List<float> scale { get; set; } = new List<float> { 1, 1, 1 };

    }



    public struct SaveProperty
    {
        public string key { get; set; }
        public string value { get; set; }

        public SaveProperty(string k, string v)
        {
            key = k;
            value = v;
        }
    }

    public class Fixture
    {
        public string name { get; set; } = "";
        public uint itemId { get; set; } = 0;
        public string level { get; set; } = "";
        public string type { get; set; } = "";
    }

    public class Furniture
    {
        public string name { get; set; }

        public uint itemId { get; set; }

        public Transform transform { get; set; } = new Transform();

        public List<SaveProperty> properties { get; set; } = new List<SaveProperty>();

        public Color GetColor()
        {
            foreach (var prop in properties)
            {
                if (prop.key.Equals("Color"))
                {
                    return System.Drawing.ColorTranslator.FromHtml("#" + prop.value.Substring(0, 6));
                }
            }

            return Color.Empty;
        }

        int ColorDiff(Color c1, Color c2)
        {
            return (int)Math.Sqrt((c1.R - c2.R) * (c1.R - c2.R)
                                   + (c1.G - c2.G) * (c1.G - c2.G)
                                   + (c1.B - c2.B) * (c1.B - c2.B));
        }

        public uint GetClosestStain(List<(Color, uint)> colorList)
        {
            var color = GetColor();
            var minDist = 2000;
            uint closestStain = 0;

            foreach (var testTuple in colorList)
            {
                var currentDist = ColorDiff(testTuple.Item1, color);
                if (currentDist < minDist)
                {
                    minDist = currentDist;
                    closestStain = testTuple.Item2;
                }
            }
            return closestStain;
        }
    }

    public class Layout
    {
        public Transform playerTransform { get; set; } = new Transform();

        public string houseSize { get; set; }

        public float interiorScale { get; set; } = 1;

        public List<Fixture> interiorFixture { get; set; } = new List<Fixture>();

        public List<Furniture> interiorFurniture { get; set; } = new List<Furniture>();

        public float exteriorScale { get; set; } = 1;

        public List<Fixture> exteriorFixture { get; set; } = new List<Fixture>();

        public List<Furniture> exteriorFurniture { get; set; } = new List<Furniture>();

    }


    public class SaveLayoutManager
    {
        public ChatGui chat;
        public static Configuration Config;

        public static List<(Color, uint)> ColorList;

        public SaveLayoutManager(ChatGui chatGui, Configuration config)
        {
            chat = chatGui;
            Config = config;
        }

        public static float layoutScale = 1;


        private static float ParseFloat(string floatString)
        {
            var updatedString = floatString.Replace(",", CultureInfo.InvariantCulture.NumberFormat.NumberDecimalSeparator);

            return float.Parse(updatedString, NumberStyles.Any, CultureInfo.InvariantCulture);
        }

        private float scale(float i)
        {

            return checkZero(i);
        }

        private static float descale(float i)
        {
            return i / layoutScale;
        }

        private float checkZero(float i)
        {
            if (Math.Abs(i) < 0.001) return 0;
            return i;
        }

        List<float> RotationToQuat(float rotation)
        {
            Quaternion q = Quaternion.CreateFromYawPitchRoll(0, 0, rotation);

            return new List<float> { checkZero(q.X), checkZero(q.Y), checkZero(q.Z), checkZero(q.W) };
        }


        static void ImportFurniture(List<HousingItem> itemList, List<Furniture> furnitureList)
        {
            var ItemSheet = Data.GetExcelSheet<Item>();

            foreach (Furniture furniture in furnitureList)
            {
                var itemRow = ItemSheet.FirstOrDefault(row => row.Name.ToString().Equals(furniture.name));

                if (itemRow == null) continue;

                var r = furniture.transform.rotation;
                var quat = new Quaternion(r[0], r[1], r[2], r[3]);

                var houseItem = new HousingItem(
                    itemRow.RowId,
                    (byte)furniture.GetClosestStain(ColorList),
                    descale(furniture.transform.location[0]),
                    descale(furniture.transform.location[2]), // switch Y & Z axis
                    descale(furniture.transform.location[1]),
                    -QuaternionExtensions.ComputeZAngle(quat),
                    furniture.name);

                itemList.Add(houseItem);
            }
        }

        public static void ImportLayout(string path)
        {

            string jsonString = File.ReadAllText(path);

            Layout layout = JsonSerializer.Deserialize<Layout>(jsonString);


            var StainList = Data.GetExcelSheet<Stain>();

            ColorList = new List<(Color, uint)>();

            foreach (var stain in StainList)
            {
                var color = Utils.StainToVector4(stain.Color);
                ColorList.Add((Color.FromArgb((int)stain.Color), stain.RowId));
            }


            Config.InteriorItemList.Clear();
            layoutScale = layout.interiorScale;
            ImportFurniture(Config.InteriorItemList, layout.interiorFurniture);

            Config.ExteriorItemList.Clear();
            layoutScale = layout.exteriorScale;
            ImportFurniture(Config.ExteriorItemList, layout.exteriorFurniture);

            Config.Layout = layout;

        }

        public static void LoadInteriorFixtues()
        {
            var layout = Config.Layout;

            layout.interiorFixture.Clear();

            for (var i = 0; i < IndoorAreaData.FloorMax; i++)
            {
                var fixtures = Memory.Instance.GetInteriorCommonFixtures(i);
                if (fixtures.Length == 0) continue;

                for (var j = 0; j < IndoorFloorData.PartsMax; j++)
                {
                    if (fixtures[j].FixtureKey == -1 || fixtures[j].FixtureKey == 0) continue;
                    if (fixtures[j].Item == null) continue;

                    var fixture = new Fixture();
                    fixture.type = Utils.GetInteriorPartDescriptor((InteriorPartsType)j);
                    fixture.level = Utils.GetFloorDescriptor((InteriorFloor)i);
                    fixture.name = fixtures[j].Item.Name.ToString();
                    fixture.itemId = fixtures[j].Item.RowId;

                    layout.interiorFixture.Add(fixture);
                }
            }

            var territoryId = Memory.Instance.GetTerritoryTypeId();
            var row = MakePlacePlugin.Data.GetExcelSheet<TerritoryType>().GetRow(territoryId);

            if (row != null)
            {
                var placeName = row.PlaceName.Value.Name.ToString();

                if (placeName.Contains("Apartment"))
                {
                    layout.houseSize = "Apartment";

                    var area = placeName.Replace("Apartment", "");

                    switch (area.Trim())
                    {
                        case "Sultana's Breath":
                            area = "Goblet";
                            break;
                        case "Topmast":
                            area = "Mist";
                            break;
                        case "Lily Hills":
                            area = "Lavender Beds";
                            break;
                        case "Kobai Goten":
                            area = "Shirogane";
                            break;
                        default:
                            break;
                    }

                    var district = new Fixture();
                    district.type = "District";
                    district.name = area;
                    layout.interiorFixture.Add(district);

                }
                else
                {
                    var names = placeName.Split('-');

                    string sizeString = "";

                    switch (names[0].Trim())
                    {
                        case "Private Cottage":
                            sizeString = "Small";
                            break;
                        case "Private House":
                            sizeString = "Medium";
                            break;
                        case "Private Mansion":
                            sizeString = "Large";
                            break;
                        case "Private Chambers":
                            sizeString = "Apartment";
                            break;
                        default:
                            break;
                    }

                    layout.houseSize = sizeString;

                    if (names.Length > 1)
                    {
                        var district = new Fixture();
                        district.type = "District";
                        district.name = names[1].Replace("The", "").Trim();
                        layout.interiorFixture.Add(district);

                    }
                }
            }

        }

        void RecordFurniture(List<Furniture> furnitureList, List<HousingItem> itemList)
        {
            HousingData Data = HousingData.Instance;
            furnitureList.Clear();
            foreach (HousingItem gameObject in itemList)
            {

                var furniture = new Furniture();

                furniture.name = gameObject.Name;
                furniture.itemId = gameObject.ItemKey;
                furniture.transform.location = new List<float> { scale(gameObject.X), scale(gameObject.Z), scale(gameObject.Y) };
                furniture.transform.rotation = RotationToQuat(-gameObject.Rotate);

                if (gameObject.Stain != 0 && Data.TryGetStain(gameObject.Stain, out var stainColor))
                {

                    var color = Utils.StainToVector4(stainColor.Color);
                    var cr = (int)(color.X * 255);
                    var cg = (int)(color.Y * 255);
                    var cb = (int)(color.Z * 255);
                    var ca = (int)(color.W * 255);

                    furniture.properties.Add(new SaveProperty("Color", $"{cr:X2}{cg:X2}{cb:X2}{ca:X2}"));

                }

                furnitureList.Add(furniture);
            }

        }

        public void ExportLayout()
        {



            Layout save = Config.Layout;
            save.playerTransform.location = new List<float> { 0, 0, 0 };
            save.playerTransform.rotation = RotationToQuat(0);

            save.interiorScale = 1;

            RecordFurniture(save.interiorFurniture, Config.InteriorItemList);
            RecordFurniture(save.exteriorFurniture, Config.ExteriorItemList);


            var encoderSettings = new TextEncoderSettings();
            encoderSettings.AllowCharacters('\'');
            encoderSettings.AllowRange(UnicodeRanges.BasicLatin);

            var options = new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            };
            string jsonString = JsonSerializer.Serialize(save, options);

            string pattern = @"\s+(-?(?:[0-9]*[.])?[0-9]+(?:E-[0-9]+)?,?)\s*(?=\s[-\d\]])";
            string result = Regex.Replace(jsonString, pattern, " $1");

            File.WriteAllText(Config.SaveLocation, result);


            Log("Finished exporting layout");
        }

    }
}