﻿// xivModdingFramework
// Copyright © 2018 Rafael Gonzalez - All Rights Reserved
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using SharpDX;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HelixToolkit.SharpDX.Core;
using xivModdingFramework.General.Enums;
using xivModdingFramework.Helpers;
using xivModdingFramework.Items;
using xivModdingFramework.Items.Enums;
using xivModdingFramework.Items.Interfaces;
using xivModdingFramework.Models.DataContainers;
using xivModdingFramework.Models.Enums;
using xivModdingFramework.Mods;
using xivModdingFramework.Resources;
using xivModdingFramework.SqPack.FileTypes;
using BoundingBox = xivModdingFramework.Models.DataContainers.BoundingBox;
using System.Diagnostics;
using xivModdingFramework.Items.Categories;
using System.Threading;
using xivModdingFramework.Models.Helpers;
using Newtonsoft.Json;
using xivModdingFramework.Materials.FileTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using xivModdingFramework.Textures.FileTypes;
using xivModdingFramework.Models.ModelTextures;
using xivModdingFramework.Variants.FileTypes;
using SixLabors.ImageSharp.Formats.Png;
using System.Text.RegularExpressions;
using xivModdingFramework.Cache;
using xivModdingFramework.SqPack.DataContainers;
using xivModdingFramework.Mods.DataContainers;
using xivModdingFramework.Mods.FileTypes;
using Index = xivModdingFramework.SqPack.FileTypes.Index;
using System.Data.SQLite;
using static xivModdingFramework.Cache.XivCache;
using System.Security.Cryptography;

namespace xivModdingFramework.Models.FileTypes
{
    public class Mdl
    {
        private const string MdlExtension = ".mdl";
        private readonly DirectoryInfo _gameDirectory;

        // Simple internal use hashable pair of Halfs.
        private struct HalfUV
        {
            public HalfUV(Half _x, Half _y)
            {
                x = _x;
                y = _y;
            }
            public HalfUV(float _x, float _y)
            {
                x = _x;
                y = _y;
            }

            public Half x;
            public Half y;

            public override int GetHashCode()
            {
                var bx = BitConverter.GetBytes(x);
                var by = BitConverter.GetBytes(y);

                var bytes = new byte[4];
                bytes[0] = bx[0];
                bytes[1] = bx[1];
                bytes[2] = by[0];
                bytes[3] = by[1];

                return BitConverter.ToInt32(bytes, 0);
            }
        }

        private static Dictionary<string, HashSet<HalfUV>> BodyHashes;

        // Retrieve hash list of UVs for use in heuristics.
        private HashSet<HalfUV> GetUVHashSet(string key)
        {
            if (BodyHashes == null)
            {
                BodyHashes = new Dictionary<string, HashSet<HalfUV>>();
            }

            if (BodyHashes.ContainsKey(key))
            {
                return BodyHashes[key];
            }

            try
            {

                var connectString = "Data Source=resources/db/uv_heuristics.db;";
                using (var db = new SQLiteConnection(connectString))
                {
                    db.Open();

                    // Time to go root hunting.
                    var query = "select * from " + key + ";";

                    using (var cmd = new SQLiteCommand(query, db))
                    {
                        var uvs = new HashSet<HalfUV>();
                        using (var reader = new CacheReader(cmd.ExecuteReader()))
                        {
                            while (reader.NextRow())
                            {
                                var uv = new HalfUV();
                                uv.x = reader.GetFloat("x");
                                uv.y = reader.GetFloat("y");
                                uvs.Add(uv);
                            }
                        }
                        BodyHashes[key] = uvs;
                        return BodyHashes[key];
                    }
                }
            }
            catch (Exception ex)
            {
                // blep
            }

            return null;
        }

        public Mdl(DirectoryInfo gameDirectory)
        {
            _gameDirectory = gameDirectory;
        }

        private byte[] _rawData;

        /// <summary>
        /// Retrieves and clears the RawData value.
        /// </summary>
        /// <returns></returns>
        public byte[] GetRawData()
        {
            var ret = _rawData;
            _rawData = null;
            return ret;
        }

        private static List<List<ShapeData.ShapeDataEntry>> _SavedData = new List<List<ShapeData.ShapeDataEntry>>();
        private static List<List<Vector3>> _SavedVectorArrays = new List<List<Vector3>>();

        /// <summary>
        /// Converts and exports an item's MDL file, passing it to the appropriate exporter as necessary
        /// to match the target file extention.
        /// </summary>
        /// <param name="item"></param>
        /// <param name="race"></param>
        /// <param name="submeshId"></param>
        /// <returns></returns>
        public async Task ExportMdlToFile(IItemModel item, XivRace race, string outputFilePath, string submeshId = null, bool includeTextures = true, bool getOriginal = false)
        {
            var mdlPath = await GetMdlPath(item, race, submeshId);
            var mtrlVariant = 1;
            try
            {
                var _imc = new Imc(_gameDirectory);
                mtrlVariant = (await _imc.GetImcInfo(item)).MaterialSet;
            }
            catch (Exception ex)
            {
                // No-op, defaulted to 1.
            }

            await ExportMdlToFile(mdlPath, outputFilePath, mtrlVariant, includeTextures, getOriginal);
        }


        /// <summary>
        /// Converts and exports an item's MDL file, passing it to the appropriate exporter as necessary
        /// to match the target file extention.
        /// </summary>
        /// <param name="mdlPath"></param>
        /// <param name="outputFilePath"></param>
        /// <param name="getOriginal"></param>
        /// <returns></returns>
        public async Task ExportMdlToFile(string mdlPath, string outputFilePath, int mtrlVariant = 1, bool includeTextures = true, bool getOriginal = false)
        {
            // Importers and exporters currently use the same criteria.
            // Any available exporter is assumed to be able to import and vice versa.
            // This may change at a later date.
            var exporters = GetAvailableExporters();
            var fileFormat = Path.GetExtension(outputFilePath).Substring(1);
            fileFormat = fileFormat.ToLower();
            if (!exporters.Contains(fileFormat))
            {
                throw new NotSupportedException(fileFormat.ToUpper() + " File type not supported.");
            }

            var dir = Path.GetDirectoryName(outputFilePath);
            if (!Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            var imc = new Imc(_gameDirectory);
            var model = await GetModel(mdlPath);
            await ExportModel(model, outputFilePath, mtrlVariant, includeTextures);
        }


        /// <summary>
        /// Exports a TTModel file to the given output path.
        /// </summary>
        /// <param name="model"></param>
        /// <param name="outputFilePath"></param>
        /// <returns></returns>
        public async Task ExportModel(TTModel model, string outputFilePath, int mtrlVariant = 1, bool includeTextures = true)
        {
            var exporters = GetAvailableExporters();
            var fileFormat = Path.GetExtension(outputFilePath).Substring(1);
            fileFormat = fileFormat.ToLower();
            if (!exporters.Contains(fileFormat))
            {
                throw new NotSupportedException(fileFormat.ToUpper() + " File type not supported.");
            }



            var dir = Path.GetDirectoryName(outputFilePath);
            if (!Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            // Remove the existing file if it exists, so that the user doesn't get confused thinking an old file is the new one.
            File.Delete(outputFilePath);

            outputFilePath = outputFilePath.Replace("/", "\\");

            // OBJ is a bit of a special, speedy case.  The format both has no textures, and no weights,
            // So we don't need to do any heavy lifting for that stuff.
            if (fileFormat == "obj")
            {
                var obj = new Obj(_gameDirectory);
                obj.ExportObj(model, outputFilePath);
                return;
            }

            if (!model.IsInternal)
            {
                // This isn't *really* true, but there's no case where we are re-exporting TTModel objects
                // right now without them at least having an internal XIV path associated, so I don't see a need to fuss over this,
                // since it would be complicated.
                throw new NotSupportedException("Cannot export non-internal model - Skel data unidentifiable.");
            }

            // The export process could really be sped up by forking threads to do
            // both the bone and material exports at the same time.

            // Pop the textures out so the exporters can reference them.
            if (includeTextures)
            {
                // Fix up our skin references in the model before exporting, to ensure
                // we supply the right material names to the exporters down-chain.
                if (model.IsInternal)
                {
                    ModelModifiers.FixUpSkinReferences(model, model.Source, null);
                }
                await ExportMaterialsForModel(model, outputFilePath, _gameDirectory, mtrlVariant);
            }



            // Save the DB file.

            var cwd = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            var converterFolder = cwd + "\\converters\\" + fileFormat;
            Directory.CreateDirectory(converterFolder);
            var dbPath = converterFolder + "\\input.db";
            model.SaveToFile(dbPath, outputFilePath);


            if (fileFormat == "db")
            {
                // Just want the intermediate file? Just see if we need to move it.
                if (!Path.Equals(outputFilePath, dbPath))
                {
                    File.Delete(outputFilePath);
                    File.Move(dbPath, outputFilePath);
                }
            }
            else
            {
                // We actually have an external importer to use.

                // We don't really care that much about showing the user a log
                // during exports, so we can just do this the simple way.

                var outputFile = converterFolder + "\\result." + fileFormat;

                // Get rid of any existing intermediate output file, in case it causes problems for any converters.
                File.Delete(outputFile);

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = converterFolder + "\\converter.exe",
                        Arguments = "\"" + dbPath + "\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        WorkingDirectory = "" + converterFolder + "",
                        CreateNoWindow = true
                    }
                };

                proc.Start();
                proc.WaitForExit();
                var code = proc.ExitCode;

                if (code != 0)
                {
                    throw new Exception("Exporter threw error code: " + proc.ExitCode);
                }

                // Just move the result file if we need to.
                if (!Path.Equals(outputFilePath, outputFile))
                {
                    File.Delete(outputFilePath);
                    File.Move(outputFile, outputFilePath);
                }
            }
        }

        /// <summary>
        /// Retrieves and exports the materials for the current model, to be used alongside ExportModel
        /// </summary>
        public static async Task ExportMaterialsForModel(TTModel model, string outputFilePath, DirectoryInfo gameDirectory, int mtrlVariant = 1, XivRace targetRace = XivRace.All_Races)
        {
            var modelName = Path.GetFileNameWithoutExtension(model.Source);
            var directory = Path.GetDirectoryName(outputFilePath);

            // Language doesn't actually matter here.
            var _mtrl = new Mtrl(XivCache.GameInfo.GameDirectory);
            var _tex = new Tex(gameDirectory);
            var _index = new Index(gameDirectory);
            var materialIdx = 0;


            foreach (var materialName in model.Materials)
            {
                try
                {
                    var mdlPath = model.Source;

                    // Set source race to match so that it doesn't get replaced
                    if (targetRace != XivRace.All_Races)
                    {
                        var bodyRegex = new Regex("(b[0-9]{4})");
                        var faceRegex = new Regex("(f[0-9]{4})");
                        var tailRegex = new Regex("(t[0-9]{4})");

                        if (bodyRegex.Match(materialName).Success)
                        {
                            var currentRace = model.Source.Substring(model.Source.LastIndexOf('c') + 1, 4);
                            mdlPath = model.Source.Replace(currentRace, targetRace.GetRaceCode());
                        }

                        var faceMatch = faceRegex.Match(materialName);
                        if (faceMatch.Success)
                        {
                            var mdlFace = faceRegex.Match(model.Source).Value;

                            mdlPath = model.Source.Replace(mdlFace, faceMatch.Value);
                        }

                        var tailMatch = tailRegex.Match(materialName);
                        if (tailMatch.Success)
                        {
                            var mdlTail = tailRegex.Match(model.Source).Value;

                            mdlPath = model.Source.Replace(mdlTail, tailMatch.Value);
                        }
                    }

                    // This messy sequence is ultimately to get access to _modelMaps.GetModelMaps().
                    var mtrlPath = _mtrl.GetMtrlPath(mdlPath, materialName, mtrlVariant);
                    var mtrlOffset = await _index.GetDataOffset(mtrlPath);
                    var mtrl = await _mtrl.GetMtrlData(mtrlOffset, mtrlPath);
                    var modelMaps = await ModelTexture.GetModelMaps(gameDirectory, mtrl);

                    // Outgoing file names.
                    var mtrl_prefix = directory + "\\" + Path.GetFileNameWithoutExtension(materialName.Substring(1)) + "_";
                    var mtrl_suffix = ".png";

                    if (modelMaps.Diffuse != null && modelMaps.Diffuse.Length > 0)
                    {
                        using (Image<Rgba32> img = Image.LoadPixelData<Rgba32>(modelMaps.Diffuse, modelMaps.Width, modelMaps.Height))
                        {
                            img.Save(mtrl_prefix + "d" + mtrl_suffix, new PngEncoder());
                        }
                    }

                    if (modelMaps.Normal != null && modelMaps.Diffuse.Length > 0)
                    {
                        using (Image<Rgba32> img = Image.LoadPixelData<Rgba32>(modelMaps.Normal, modelMaps.Width, modelMaps.Height))
                        {
                            img.Save(mtrl_prefix + "n" + mtrl_suffix, new PngEncoder());
                        }
                    }

                    if (modelMaps.Specular != null && modelMaps.Diffuse.Length > 0)
                    {
                        using (Image<Rgba32> img = Image.LoadPixelData<Rgba32>(modelMaps.Specular, modelMaps.Width, modelMaps.Height))
                        {
                            img.Save(mtrl_prefix + "s" + mtrl_suffix, new PngEncoder());
                        }
                    }

                    if (modelMaps.Alpha != null && modelMaps.Diffuse.Length > 0)
                    {
                        using (Image<Rgba32> img = Image.LoadPixelData<Rgba32>(modelMaps.Alpha, modelMaps.Width, modelMaps.Height))
                        {
                            img.Save(mtrl_prefix + "o" + mtrl_suffix, new PngEncoder());
                        }
                    }

                    if (modelMaps.Emissive != null && modelMaps.Diffuse.Length > 0)
                    {
                        using (Image<Rgba32> img = Image.LoadPixelData<Rgba32>(modelMaps.Emissive, modelMaps.Width, modelMaps.Height))
                        {
                            img.Save(mtrl_prefix + "e" + mtrl_suffix, new PngEncoder());
                        }
                    }

                }
                catch (Exception exc)
                {
                    // Failing to resolve a material is considered a non-critical error.
                    // Continue attempting to resolve the rest of the materials in the model.
                    //throw exc;
                }
                materialIdx++;
            }
        }

        /// <summary>
        /// Retreives the high level TTModel representation of an underlying MDL file.
        /// </summary>
        /// <param name="item"></param>
        /// <param name="race"></param>
        /// <param name="submeshId"></param>
        /// <param name="getOriginal"></param>
        /// <returns></returns>
        public async Task<TTModel> GetModel(IItemModel item, XivRace race, string submeshId = null, bool getOriginal = false)
        {
            var index = new Index(_gameDirectory);
            var dat = new Dat(_gameDirectory);
            var modding = new Modding(_gameDirectory);
            var mdl = await GetRawMdlData(item, race, submeshId, getOriginal);
            var ttModel = TTModel.FromRaw(mdl);
            return ttModel;
        }
        public async Task<TTModel> GetModel(string mdlPath, bool getOriginal = false)
        {
            var mdl = await GetRawMdlData(mdlPath, getOriginal);
            var ttModel = TTModel.FromRaw(mdl);
            return ttModel;
        }

        public async Task<XivMdl> GetRawMdlData(IItemModel item, XivRace race, string submeshId = null, bool getOriginal = false)
        {
            var mdlPath = await GetMdlPath(item, race, submeshId);
            return await GetRawMdlData(mdlPath, getOriginal);
        }

        // Some constant pointers/sizes
        private const int _MdlHeaderSize = 0x44; // 68 Decimal
        private const int _vertexDataHeaderSize = 0x88; // 136 Decimal


        /// <summary>
        /// Retrieves the raw XivMdl file at a given internal file path.
        /// 
        /// If it an explicit offset is provided, it will be used over path or mod offset resolution.
        /// </summary>
        /// <returns>An XivMdl structure containing all mdl data.</returns>
        public async Task<XivMdl> GetRawMdlData(string mdlPath, bool getOriginal = false, long offset = 0)
        {

            var dat = new Dat(_gameDirectory);
            var modding = new Modding(_gameDirectory);
            var mod = await modding.TryGetModEntry(mdlPath);
            var modded = mod != null && mod.enabled;
            var getShapeData = true;


            if (offset == 0)
            {
                var index = new Index(_gameDirectory);
                offset = await index.GetDataOffset(mdlPath);

                if (getOriginal)
                {
                    if (modded)
                    {
                        offset = mod.data.originalOffset;
                        modded = false;
                    }
                }
            }


            if (offset == 0)
            {
                return null;
            }

            var mdlData = await dat.GetType3Data(offset, IOUtil.GetDataFileFromPath(mdlPath));
            //File.WriteAllBytes("D:\\aaaa.mdl", mdlData.Data);

            var xivMdl = new XivMdl { MdlPath = mdlPath };
            int totalNonNullMaterials = 0;


            // Calculated offsets
            int _endOfVertexDataHeaders = _MdlHeaderSize + (_vertexDataHeaderSize * mdlData.MeshCount);

            using (var br = new BinaryReader(new MemoryStream(mdlData.Data)))
            {
                var version = br.ReadUInt16();
                var val2 = br.ReadUInt16();
                var mdlSignature = 0;

                int mdlVersion = version >= 6 ? 6 : 5;
                xivMdl.MdlVersion = version;

                // We skip the Vertex Data Structures for now
                // This is done so that we can get the correct number of meshes per LoD first
                br.BaseStream.Seek(_endOfVertexDataHeaders, SeekOrigin.Begin);

                var mdlPathData = new MdlPathData()
                {
                    PathCount = br.ReadInt32(),
                    PathBlockSize = br.ReadInt32(),
                    AttributeList = new List<string>(),
                    BoneList = new List<string>(),
                    MaterialList = new List<string>(),
                    ShapeList = new List<string>(),
                    ExtraPathList = new List<string>()
                };

                // Get the entire path string block to parse later
                // This will be done when we obtain the path counts for each type
                var pathBlock = br.ReadBytes(mdlPathData.PathBlockSize);

                var mdlModelData = new MdlModelData
                {
                    Radius = br.ReadSingle(),

                    MeshCount = br.ReadInt16(),
                    AttributeCount = br.ReadInt16(),
                    MeshPartCount = br.ReadInt16(),
                    MaterialCount = br.ReadInt16(),

                    BoneCount = br.ReadInt16(),
                    BoneListCount = br.ReadInt16(),

                    ShapeCount = br.ReadInt16(),
                    ShapePartCount = br.ReadInt16(),
                    ShapeDataCount = br.ReadUInt16(),

                    LoDCount = br.ReadByte(),

                    Flags1 = br.ReadByte(),

                    ElementIdCount = br.ReadUInt16(),
                    TerrainShadowMeshCount = br.ReadByte(),

                    Flags2 = br.ReadByte(),

                    ModelClipOutDistance = br.ReadSingle(),
                    ShadowClipOutDistance = br.ReadSingle(),

                    UnknownBoundingBoxCount = br.ReadUInt16(),
                    TerrainShadowSubmeshCount = br.ReadInt16(),
                    Unknown10a = br.ReadByte(),

                    BgChangeMaterialIndex = br.ReadByte(),
                    BgCrestChangeMaterialIndex = br.ReadByte(),

                    Unknown12 = br.ReadByte(),
                    BoneSetSize = br.ReadInt16(),

                    Unknown13 = br.ReadInt16(),
                    Unknown14 = br.ReadInt16(),
                    Unknown15 = br.ReadInt16(),
                    Unknown16 = br.ReadInt16(),
                    Unknown17 = br.ReadInt16()
                };

                // Finished reading all MdlModelData
                // Adding to xivMdl
                xivMdl.ModelData = mdlModelData;

                // Now that we have the path counts wee can parse the path strings
                using (var br1 = new BinaryReader(new MemoryStream(pathBlock)))
                {
                    // Attribute Paths
                    for (var i = 0; i < mdlModelData.AttributeCount; i++)
                    {
                        // Because we don't know the length of the string, we read the data until we reach a 0 value
                        // That 0 value is the space between strings
                        byte a;
                        var atrName = new List<byte>();
                        while ((a = br1.ReadByte()) != 0)
                        {
                            atrName.Add(a);
                        }

                        // Read the string from the byte array and remove null terminators
                        var atr = Encoding.ASCII.GetString(atrName.ToArray()).Replace("\0", "");

                        // Add the attribute to the list
                        mdlPathData.AttributeList.Add(atr);
                    }

                    // Bone Paths
                    for (var i = 0; i < mdlModelData.BoneCount; i++)
                    {
                        byte a;
                        var boneName = new List<byte>();
                        while ((a = br1.ReadByte()) != 0)
                        {
                            boneName.Add(a);
                        }

                        var bone = Encoding.ASCII.GetString(boneName.ToArray()).Replace("\0", "");

                        mdlPathData.BoneList.Add(bone);
                    }

                    // Material Paths
                    for (var i = 0; i < mdlModelData.MaterialCount; i++)
                    {
                        byte a;
                        var materialName = new List<byte>();
                        while ((a = br1.ReadByte()) != 0)
                        {
                            materialName.Add(a);
                        }

                        var mat = Encoding.ASCII.GetString(materialName.ToArray()).Replace("\0", "");
                        if (mat.StartsWith("shp_"))
                        {
                            // Catch case for situation where there's null values at the end of the materials list.
                            mdlPathData.ShapeList.Add(mat);
                        }
                        else
                        {
                            totalNonNullMaterials++;
                            mdlPathData.MaterialList.Add(mat);
                        }
                    }


                    // Shape Paths
                    for (var i = 0; i < mdlModelData.ShapeCount; i++)
                    {
                        byte a;
                        var shapeName = new List<byte>();
                        while ((a = br1.ReadByte()) != 0)
                        {
                            shapeName.Add(a);
                        }

                        var shp = Encoding.ASCII.GetString(shapeName.ToArray()).Replace("\0", "");

                        mdlPathData.ShapeList.Add(shp);
                    }

                    var remainingPathData = mdlPathData.PathBlockSize - br1.BaseStream.Position;
                    if (remainingPathData > 2)
                    {
                        while (remainingPathData != 0)
                        {
                            byte a;
                            var extraName = new List<byte>();
                            while ((a = br1.ReadByte()) != 0)
                            {
                                extraName.Add(a);
                                remainingPathData--;
                            }

                            remainingPathData--;

                            if (extraName.Count > 0)
                            {
                                var extra = Encoding.ASCII.GetString(extraName.ToArray()).Replace("\0", "");

                                mdlPathData.ExtraPathList.Add(extra);
                            }
                        }

                    }
                }

                // Finished reading all Path Data
                // Adding to xivMdl
                xivMdl.PathData = mdlPathData;

                // Currently Unknown Data
                var unkData0 = new UnknownData0
                {
                    Unknown = br.ReadBytes(mdlModelData.ElementIdCount * 32)
                    // int ElementId
                    // int ParentboneName(?)
                    // float[3] Translate
                    // float[3] Rotate
                };

                // Finished reading all UnknownData0
                // Adding to xivMdl
                xivMdl.UnkData0 = unkData0;

                var totalLoDMeshes = 0;

                // We add each LoD to the list
                // Note: There is always 3 LoD
                xivMdl.LoDList = new List<LevelOfDetail>();
                for (var i = 0; i < 3; i++)
                {
                    var lod = new LevelOfDetail
                    {
                        MeshOffset = br.ReadUInt16(),
                        MeshCount = br.ReadInt16(),
                        ModelLoDRange = br.ReadSingle(),
                        TextureLoDRange = br.ReadSingle(),
                        WaterMeshIndex = br.ReadInt16(),
                        WaterMeshCount = br.ReadInt16(),
                        ShadowMeshIndex = br.ReadInt16(),
                        ShadowMeshCount = br.ReadInt16(),
                        TerrainShadowMeshIndex = br.ReadInt16(),
                        TerrainShadowMeshCount = br.ReadInt16(),
                        FogMeshIndex = br.ReadInt16(),
                        FogMeshCount = br.ReadInt16(),
                        EdgeGeometrySize = br.ReadInt32(),
                        EdgeGeometryOffset = br.ReadInt32(),
                        Unknown6 = br.ReadInt32(),
                        Unknown7 = br.ReadInt32(),
                        VertexDataSize = br.ReadInt32(),
                        IndexDataSize = br.ReadInt32(),
                        VertexDataOffset = br.ReadInt32(),
                        IndexDataOffset = br.ReadInt32(),
                        MeshDataList = new List<MeshData>()
                    };
                    // Finished reading LoD

                    totalLoDMeshes += lod.MeshCount;

                    // if LoD0 shows no mesh, add one (This is rare, but happens on company chest for example)
                    if (i == 0 && lod.MeshCount == 0)
                    {
                        lod.MeshCount = 1;
                    }

                    // This is a simple check to identify old mods that may have broken shape data.
                    // Old mods still have LoD 1+ data.
                    if (modded  && i > 0 && lod.MeshCount > 0)
                    {
                        getShapeData = false;
                    }

                    //Adding to xivMdl
                    xivMdl.LoDList.Add(lod);
                }

                //HACK: This is a workaround for certain furniture items, mainly with picture frames and easel
                var isEmpty = false;
                try
                {
                    isEmpty = br.PeekChar() == 0;
                }
                catch { }

                if (isEmpty && totalLoDMeshes < mdlModelData.MeshCount)
                {
                    xivMdl.ExtraLoDList = new List<LevelOfDetail>();

                    for (var i = 0; i < mdlModelData.Unknown10a; i++)
                    {
                        var lod = new LevelOfDetail
                        {
                            MeshOffset = br.ReadUInt16(),
                            MeshCount = br.ReadInt16(),
                            ModelLoDRange = br.ReadInt32(),
                            TextureLoDRange = br.ReadInt32(),
                            WaterMeshIndex = br.ReadInt16(),
                            WaterMeshCount = br.ReadInt16(),
                            ShadowMeshIndex = br.ReadInt16(),
                            ShadowMeshCount = br.ReadInt16(),
                            TerrainShadowMeshIndex = br.ReadInt16(),
                            TerrainShadowMeshCount = br.ReadInt16(),
                            FogMeshIndex = br.ReadInt16(),
                            FogMeshCount = br.ReadInt16(),
                            EdgeGeometrySize = br.ReadInt32(),
                            EdgeGeometryOffset = br.ReadInt32(),
                            Unknown6 = br.ReadInt32(),
                            Unknown7 = br.ReadInt32(),
                            VertexDataSize = br.ReadInt32(),
                            IndexDataSize = br.ReadInt32(),
                            VertexDataOffset = br.ReadInt32(),
                            IndexDataOffset = br.ReadInt32(),
                            MeshDataList = new List<MeshData>()
                        };

                        xivMdl.ExtraLoDList.Add(lod);
                    }
                }


                #region Vertex Data Structures

                // Now that we have the LoD data, we can go back and read the Vertex Data Structures
                // First we save our current position
                var savePosition = br.BaseStream.Position;

                var loDStructPos = _MdlHeaderSize;
                // for each mesh in each lod
                for (var i = 0; i < xivMdl.LoDList.Count; i++)
                {
                    var totalMeshCount = xivMdl.LoDList[i].MeshCount + xivMdl.LoDList[i].WaterMeshCount;
                    for (var j = 0; j < totalMeshCount; j++)
                    {
                        xivMdl.LoDList[i].MeshDataList.Add(new MeshData());
                        xivMdl.LoDList[i].MeshDataList[j].VertexDataStructList = new List<VertexDataStruct>();

                        // LoD Index * Vertex Data Structure size + Header

                        br.BaseStream.Seek(j * _vertexDataHeaderSize + loDStructPos, SeekOrigin.Begin);

                        // If the first byte is 255, we reached the end of the Vertex Data Structs
                        while(true)
                        {
                            // Vertex Header Reading
                            var dataBlockNum = br.ReadByte();
                            if (dataBlockNum == 255) break;

                            byte b1 = br.ReadByte();
                            byte b2 = br.ReadByte();
                            byte b3 = br.ReadByte();
                            byte b4 = br.ReadByte();
                            var padding = br.ReadBytes(3);
                            var vertexDataStruct = new VertexDataStruct
                            {
                                DataBlock = dataBlockNum,
                                DataOffset = b1,
                                DataType = VertexTypeDictionary[b2],
                                DataUsage = VertexUsageDictionary[b3],
                                Count = b4,
                            };

                            xivMdl.LoDList[i].MeshDataList[j].VertexDataStructList.Add(vertexDataStruct);

                            if(vertexDataStruct.DataUsage == VertexUsageType.BoneIndex)
                            {
                                // Store bone array size for reference later.
                                // (We could rip through the vertex list to see if any 5+ bone vertices exist, but that's expensive)
                                xivMdl.LoDList[i].MeshDataList[j].VertexBoneArraySize = vertexDataStruct.DataType == VertexDataType.UByte8 ? 8 : 4;
                            }
                        }
                    }

                    loDStructPos += 136 * xivMdl.LoDList[i].MeshCount;
                }
                

                // Now that we finished reading the Vertex Data Structures, we can go back to our saved position
                br.BaseStream.Seek(savePosition, SeekOrigin.Begin);
                #endregion

                // Mesh Data Information
                var meshNum = 0;
                for(int l = 0; l < xivMdl.LoDList.Count; l++)
                {
                    var lod = xivMdl.LoDList[l];
                    var totalMeshCount = lod.MeshCount + lod.WaterMeshCount;

                    for (var i = 0; i < totalMeshCount; i++)
                    {
                        var meshDataInfo = new MeshDataInfo
                        {
                            VertexCount = br.ReadInt32(),
                            IndexCount = br.ReadInt32(),
                            MaterialIndex = br.ReadInt16(),
                            MeshPartIndex = br.ReadInt16(),
                            MeshPartCount = br.ReadInt16(),
                            BoneSetIndex = br.ReadInt16(),
                            IndexDataOffset = br.ReadInt32(),
                            VertexDataOffset0 = br.ReadInt32(),
                            VertexDataOffset1 = br.ReadInt32(),
                            VertexDataOffset2 = br.ReadInt32(),
                            VertexDataEntrySize0 = br.ReadByte(),
                            VertexDataEntrySize1 = br.ReadByte(),
                            VertexDataEntrySize2 = br.ReadByte(),
                            VertexStreamCountUnknown = br.ReadByte()
                        };

                         lod.MeshDataList[i].MeshInfo = meshDataInfo;

                        // In the event we have a null material reference, set it to material 0 to be safe.
                        if (meshDataInfo.MaterialIndex >= totalNonNullMaterials)
                        {
                            meshDataInfo.MaterialIndex = 0;
                        }

                        var materialString = xivMdl.PathData.MaterialList[meshDataInfo.MaterialIndex];
                        // Try block to cover odd cases like Au Ra Male Face #92 where for some reason the
                        // Last LoD points to using a shp for a material for some reason.
                        try
                        {
                            var typeChar = materialString[4].ToString() + materialString[9].ToString();

                            if (typeChar.Equals("cb"))
                            {
                                lod.MeshDataList[i].IsBody = true;
                            }
                        }
                        catch (Exception e)
                        {

                        }

                        meshNum++;
                    }
                }

                // Data block for attributes offset paths
                var attributeDataBlock = new AttributeDataBlock
                {
                    AttributePathOffsetList = new List<int>(xivMdl.ModelData.AttributeCount)
                };

                for (var i = 0; i < xivMdl.ModelData.AttributeCount; i++)
                {
                    attributeDataBlock.AttributePathOffsetList.Add(br.ReadInt32());
                }

                xivMdl.AttrDataBlock = attributeDataBlock;

                // Unknown data block
                // This is commented out to allow housing items to display, the data does not exist for housing items
                // more investigation needed as to what this data is
                var unkData1 = new UnknownData1
                {
                    //Unknown = br.ReadBytes(xivMdl.ModelData.Unknown3 * 20)
                };
                xivMdl.UnkData1 = unkData1;

                // Mesh Parts
                for(int l = 0; l < xivMdl.LoDList.Count; l++)
                {
                    var lod = xivMdl.LoDList[l];
                    foreach (var meshData in lod.MeshDataList)
                    {
                        meshData.MeshPartList = new List<MeshPart>();

                        for (var i = 0; i < meshData.MeshInfo.MeshPartCount; i++)
                        {
                            // SUS - Some value differences here
                            var meshPart = new MeshPart
                            {
                                IndexOffset = br.ReadInt32(),
                                IndexCount = br.ReadInt32(),
                                AttributeBitmask = br.ReadUInt32(),
                                BoneStartOffset = br.ReadInt16(),
                                BoneCount = br.ReadInt16()
                            };

                            meshData.MeshPartList.Add(meshPart);
                        }
                    }
                }

                // Unknown data block
                var unkData2 = new UnknownData2
                {
                    Unknown = br.ReadBytes(xivMdl.ModelData.TerrainShadowSubmeshCount * 12)
                };
                xivMdl.UnkData2 = unkData2;

                // Data block for materials
                // Currently unknown usage
                var matDataBlock = new MaterialDataBlock
                {
                    MaterialPathOffsetList = new List<int>(xivMdl.ModelData.MaterialCount)
                };

                for (var i = 0; i < xivMdl.ModelData.MaterialCount; i++)
                {
                    matDataBlock.MaterialPathOffsetList.Add(br.ReadInt32());
                }

                xivMdl.MatDataBlock = matDataBlock;


                #region Bone Data Block
                var boneDataBlock = new BoneDataBlock
                {
                    BonePathOffsetList = new List<int>(xivMdl.ModelData.BoneCount)
                };

                for (var i = 0; i < xivMdl.ModelData.BoneCount; i++)
                {
                    boneDataBlock.BonePathOffsetList.Add(br.ReadInt32());
                }

                xivMdl.BoneDataBlock = boneDataBlock;
                // Bone Lists
                xivMdl.MeshBoneSets = new List<BoneSet>();
                if (mdlVersion >= 6) // Mdl Version 6
                {
                    var boneIndexMetaTable = new List<short[]>();

                    for (var i = 0; i < xivMdl.ModelData.BoneListCount; ++i)
                    {
                        boneIndexMetaTable.Add(new short[2] { br.ReadInt16(), br.ReadInt16() });
                    }

                    for (var i = 0; i < xivMdl.ModelData.BoneListCount; ++i)
                    {
                        var boneCount = boneIndexMetaTable[i][1];
                        var boneIndexMesh = new BoneSet
                        {
                            BoneIndices = new List<short>(boneCount)
                        };

                        for (var j = 0; j < boneCount; j++)
                        {
                            boneIndexMesh.BoneIndices.Add(br.ReadInt16());
                        }

                        // Eat another value for alignment to 4 bytes
                        if (boneCount % 2 == 1)
                            br.ReadInt16();

                        boneIndexMesh.BoneIndexCount = boneCount;

                        xivMdl.MeshBoneSets.Add(boneIndexMesh);
                    }
                }
                else // Mdl Version 5
                {
                    // SUS - Value differences here.
                    for (var i = 0; i < xivMdl.ModelData.BoneListCount; i++)
                    {
                        var boneIndexMesh = new BoneSet
                        {
                            BoneIndices = new List<short>(64)
                        };

                        for (var j = 0; j < 64; j++)
                        {
                            boneIndexMesh.BoneIndices.Add(br.ReadInt16());
                        }

                        boneIndexMesh.BoneIndexCount = br.ReadInt32();

                        xivMdl.MeshBoneSets.Add(boneIndexMesh);
                    }
                }

                #endregion

                #region Shape Data Block
                var shapeDataLists = new ShapeData
                {
                    ShapeInfoList = new List<ShapeData.ShapeInfo>(),
                    ShapeParts = new List<ShapeData.ShapePart>(),
                    ShapeDataList = new List<ShapeData.ShapeDataEntry>()
                };

                var totalPartCount = 0;
                // Shape Info

                // Each shape has a header entry, then a per-lod entry.
                for (var i = 0; i < xivMdl.ModelData.ShapeCount; i++)
                {

                    // Header - Offset to the shape name.
                    var shapeInfo = new ShapeData.ShapeInfo
                    {
                        ShapeNameOffset = br.ReadInt32(),
                        Name = xivMdl.PathData.ShapeList[i],
                        ShapeLods = new List<ShapeData.ShapeLodInfo>()
                    };

                    // Per LoD entry (offset to this shape's parts in the shape set)
                    var dataInfoIndexList = new List<ushort>();
                    for (var j = 0; j < xivMdl.LoDList.Count; j++)
                    {
                        dataInfoIndexList.Add(br.ReadUInt16());
                    }

                    // Per LoD entry (number of parts in the shape set)
                    var infoPartCountList = new List<short>();
                    for (var j = 0; j < xivMdl.LoDList.Count; j++)
                    {
                        infoPartCountList.Add(br.ReadInt16());
                    }

                    for (var j = 0; j < xivMdl.LoDList.Count; j++)
                    {
                        var shapeIndexPart = new ShapeData.ShapeLodInfo
                        {
                            PartOffset = dataInfoIndexList[j],
                            PartCount = infoPartCountList[j]
                        };
                        shapeInfo.ShapeLods.Add(shapeIndexPart);
                        totalPartCount += shapeIndexPart.PartCount;
                    }

                    shapeDataLists.ShapeInfoList.Add(shapeInfo);
                }

                // Shape Index Info
                for (var i = 0; i < xivMdl.ModelData.ShapePartCount; i++)
                {
                    var shapeIndexInfo = new ShapeData.ShapePart
                    {
                        MeshIndexOffset = br.ReadInt32(),  // The offset to the index block this Shape Data should be replacing in. -- This is how Shape Data is tied to each mesh.
                        IndexCount = br.ReadInt32(),  // # of triangle indices to replace.
                        ShapeDataOffset = br.ReadInt32()   // The offset where this part should start reading in the Shape Data list.
                    };

                    shapeDataLists.ShapeParts.Add(shapeIndexInfo);
                }

                // Shape data
                for (var i = 0; i < xivMdl.ModelData.ShapeDataCount; i++)
                {
                    var shapeData = new ShapeData.ShapeDataEntry
                    {
                        BaseIndex = br.ReadUInt16(),  // Base Triangle Index we're replacing
                        ShapeVertex = br.ReadUInt16()  // The Vertex that Triangle Index should now point to instead.
                    };
                    shapeDataLists.ShapeDataList.Add(shapeData);
                }

                xivMdl.MeshShapeData = shapeDataLists;

                // Build the list of offsets so we can match it for shape data.
                var indexOffsets = new List<List<int>>();
                for (int l = 0; l < xivMdl.LoDList.Count; l++)
                {
                    indexOffsets.Add(new List<int>());
                    for (int m = 0; m < xivMdl.LoDList[l].MeshDataList.Count; m++)
                    {
                        indexOffsets[l].Add(xivMdl.LoDList[l].MeshDataList[m].MeshInfo.IndexDataOffset);
                    }

                }
                xivMdl.MeshShapeData.AssignMeshAndLodNumbers(indexOffsets);

                // Sets the boolean flag if the model has shape data
                xivMdl.HasShapeData = xivMdl.ModelData.ShapeCount > 0 && getShapeData;

                #endregion

                // Bone index for Parts
                var partBoneSet = new BoneSet
                {
                    BoneIndexCount = br.ReadInt32(),
                    BoneIndices = new List<short>()
                };

                for (var i = 0; i < partBoneSet.BoneIndexCount / 2; i++)
                {
                    partBoneSet.BoneIndices.Add(br.ReadInt16());
                }

                xivMdl.PartBoneSets = partBoneSet;

                // Padding
                xivMdl.PaddingSize = br.ReadByte();
                xivMdl.PaddedBytes = br.ReadBytes(xivMdl.PaddingSize);


                // There are 4 bounding boxes in sequence, defined by a min and max point.
                // The 4 boxes are:
                // "BoundingBox"
                // "ModelBoundingBox"
                // "WaterBoundingbox"           - Typically full 0s - Probably used by water furnishings?
                // "VerticalFogBoundingBox"     - Typically full 0s - Probably used by (???)
                xivMdl.BoundingBoxes = new List<List<Vector4>>();
                for (var i = 0; i < 4; i++)
                {
                    var minPoint = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    var maxPoint = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    xivMdl.BoundingBoxes.Add(new List<Vector4>() { minPoint, maxPoint });
                }


                // Bone Bounding Box Data.
                xivMdl.BoneBoundingBoxes = new List<List<Vector4>>();
                var transformCount = xivMdl.ModelData.BoneCount;
                if (transformCount == 0)
                {
                    transformCount = (short) xivMdl.ModelData.UnknownBoundingBoxCount;
                }

                xivMdl.BoneBoundingBoxes = new List<List<Vector4>>();
                for (var i = 0; i < transformCount; i++)
                {
                    var minPoint = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    var maxPoint = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    xivMdl.BoneBoundingBoxes.Add(new List<Vector4>() { minPoint, maxPoint });
                }

                var lodNum = 0;
                var totalMeshNum = 0;
                foreach (var lod in xivMdl.LoDList)
                {
                    if (lod.MeshCount == 0) continue;

                    var meshDataList = lod.MeshDataList;

                    if (lod.MeshOffset != totalMeshNum)
                    {
                        meshDataList = xivMdl.LoDList[lodNum + 1].MeshDataList;
                    }

                    foreach (var meshData in meshDataList)
                    {
                        // Vertex Data Reading
                        var vertexData = new VertexData
                        {
                            Positions = new Vector3Collection(),
                            BoneWeights = new List<float[]>(),
                            BoneIndices = new List<byte[]>(),
                            Normals = new Vector3Collection(),
                            BiNormals = new Vector3Collection(),
                            BiNormalHandedness = new List<byte>(),
                            Tangents = new Vector3Collection(),
                            Colors = new List<SharpDX.Color>(),
                            Colors2 = new List<SharpDX.Color>(),
                            Colors4 = new Color4Collection(),
                            TextureCoordinates0 = new Vector2Collection(),
                            TextureCoordinates1 = new Vector2Collection(),
                            Indices = new IntCollection()
                        };

                        #region Positions
                        // Get the Vertex Data Structure for positions
                        var posDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                             where vertexDataStruct.DataUsage == VertexUsageType.Position
                                             select vertexDataStruct).FirstOrDefault();

                        int vertexDataOffset;
                        int vertexDataSize;

                        if (posDataStruct != null)
                        {
                            // Determine which data block the position data is in
                            // This always seems to be in the first data block
                            switch (posDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                // Get the offset for the position data for each vertex
                                var positionOffset = lod.VertexDataOffset + vertexDataOffset + posDataStruct.DataOffset + vertexDataSize * i;

                                // Go to the Data Block
                                br.BaseStream.Seek(positionOffset, SeekOrigin.Begin);

                                Vector3 positionVector;
                                // Position data is either stored in half-floats or singles
                                if (posDataStruct.DataType == VertexDataType.Half4)
                                {
                                    var x = new SharpDX.Half(br.ReadUInt16());
                                    var y = new SharpDX.Half(br.ReadUInt16());
                                    var z = new SharpDX.Half(br.ReadUInt16());
                                    var w = new SharpDX.Half(br.ReadUInt16());

                                    positionVector = new Vector3(x, y, z);
                                }
                                else
                                {
                                    var x = br.ReadSingle();
                                    var y = br.ReadSingle();
                                    var z = br.ReadSingle();

                                    positionVector = new Vector3(x, y, z);
                                }
                                vertexData.Positions.Add(positionVector);
                            }
                        }

                        #endregion


                        #region BoneWeights

                        // Get the Vertex Data Structure for bone weights
                        var bwDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                            where vertexDataStruct.DataUsage == VertexUsageType.BoneWeight
                                            select vertexDataStruct).FirstOrDefault();

                        if (bwDataStruct != null)
                        {
                            // Determine which data block the bone weight data is in
                            // This always seems to be in the first data block
                            switch (bwDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of bone weights per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var bwOffset = lod.VertexDataOffset + vertexDataOffset + bwDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(bwOffset, SeekOrigin.Begin);

                                byte[] byteValues = bwDataStruct.DataType == VertexDataType.UByte8 ? new byte[8] : new byte[4];

                                if (bwDataStruct.DataType == VertexDataType.UByte8)
                                {
                                    // Silly low => High format
                                    byteValues[0] = br.ReadByte();
                                    byteValues[4] = br.ReadByte();
                                    byteValues[1] = br.ReadByte();
                                    byteValues[5] = br.ReadByte();
                                    byteValues[2] = br.ReadByte();
                                    byteValues[6] = br.ReadByte();
                                    byteValues[3] = br.ReadByte();
                                    byteValues[7] = br.ReadByte();
                                } else
                                {
                                    for(int z = 0; z < byteValues.Length; z++)
                                    {
                                        byteValues[z] = br.ReadByte();
                                    }
                                }

                                var floatValues = new float[byteValues.Length];
                                for(int z = 0; z < floatValues.Length; z++)
                                {
                                    floatValues[z] = byteValues[z] / 255f;
                                }

                                vertexData.BoneWeights.Add(floatValues);
                            }
                        }


                        #endregion


                        #region BoneIndices

                        // Get the Vertex Data Structure for bone indices
                        var biDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                            where vertexDataStruct.DataUsage == VertexUsageType.BoneIndex
                                            select vertexDataStruct).FirstOrDefault();

                        if (biDataStruct != null)
                        {
                            // Determine which data block the bone index data is in
                            // This always seems to be in the first data block
                            switch (biDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of bone indices per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var biOffset = lod.VertexDataOffset + vertexDataOffset + biDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(biOffset, SeekOrigin.Begin);

                                byte[] byteValues = biDataStruct.DataType == VertexDataType.UByte8 ? new byte[8] : new byte[4];

                                if (biDataStruct.DataType == VertexDataType.UByte8)
                                {
                                    // Silly low => High format
                                    byteValues[0] = br.ReadByte();
                                    byteValues[4] = br.ReadByte();
                                    byteValues[1] = br.ReadByte();
                                    byteValues[5] = br.ReadByte();
                                    byteValues[2] = br.ReadByte();
                                    byteValues[6] = br.ReadByte();
                                    byteValues[3] = br.ReadByte();
                                    byteValues[7] = br.ReadByte();
                                }
                                else
                                {
                                    for (int z = 0; z < byteValues.Length; z++)
                                    {
                                        byteValues[z] = br.ReadByte();
                                    }
                                }

                                vertexData.BoneIndices.Add(byteValues);
                            }
                        }

                        #endregion


                        #region Normals

                        // Get the Vertex Data Structure for Normals
                        var normDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                              where vertexDataStruct.DataUsage == VertexUsageType.Normal
                                              select vertexDataStruct).FirstOrDefault();

                        if (normDataStruct != null)
                        {
                            // Determine which data block the normal data is in
                            // This always seems to be in the second data block
                            switch (normDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of normals per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var normOffset = lod.VertexDataOffset + vertexDataOffset + normDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(normOffset, SeekOrigin.Begin);

                                Vector3 normalVector;
                                // Normal data is either stored in half-floats or singles
                                if (normDataStruct.DataType == VertexDataType.Half4)
                                {
                                    var x = new SharpDX.Half(br.ReadUInt16());
                                    var y = new SharpDX.Half(br.ReadUInt16());
                                    var z = new SharpDX.Half(br.ReadUInt16());
                                    var w = new SharpDX.Half(br.ReadUInt16());
                                    normalVector = new Vector3(x, y, z);
                                }
                                else
                                {
                                    var x = br.ReadSingle();
                                    var y = br.ReadSingle();
                                    var z = br.ReadSingle();

                                    normalVector = new Vector3(x, y, z);
                                }

                                vertexData.Normals.Add(normalVector);
                            }
                        }

                        #endregion


                        #region BiNormals

                        // Get the Vertex Data Structure for BiNormals
                        var biNormDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                            where vertexDataStruct.DataUsage == VertexUsageType.Binormal
                                            select vertexDataStruct).FirstOrDefault();

                        if (biNormDataStruct != null)
                        {
                            // Determine which data block the binormal data is in
                            // This always seems to be in the second data block
                            switch (biNormDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of biNormals per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var biNormOffset = lod.VertexDataOffset + vertexDataOffset + biNormDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(biNormOffset, SeekOrigin.Begin);

                                var x = br.ReadByte() * 2 / 255f - 1f;
                                var y = br.ReadByte() * 2 / 255f - 1f;
                                var z = br.ReadByte() * 2 / 255f - 1f;
                                var w = br.ReadByte();

                                vertexData.BiNormals.Add(new Vector3(x, y, z));
                                vertexData.BiNormalHandedness.Add(w);
                            }
                        }

                        #endregion

                        #region Tangents

                        // Get the Vertex Data Structure for Tangents
                        var tangentDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                                 where vertexDataStruct.DataUsage == VertexUsageType.Tangent
                                                 select vertexDataStruct).FirstOrDefault();

                        if (tangentDataStruct != null)
                        {
                            // Determine which data block the tangent data is in
                            // This always seems to be in the second data block
                            switch (tangentDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is one set of tangents per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var tangentOffset = lod.VertexDataOffset + vertexDataOffset + tangentDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(tangentOffset, SeekOrigin.Begin);

                                var x = br.ReadByte() * 2 / 255f - 1f;
                                var y = br.ReadByte() * 2 / 255f - 1f;
                                var z = br.ReadByte() * 2 / 255f - 1f;
                                var w = br.ReadByte();

                                vertexData.Tangents.Add(new Vector3(x, y, z));
                                //vertexData.TangentHandedness.Add(w);
                            }
                        }

                        #endregion


                        #region VertexColor

                        var colorDataStructs = (from vertexDataStruct in meshData.VertexDataStructList
                                                where vertexDataStruct.DataUsage == VertexUsageType.Color
                                                select vertexDataStruct).ToList();
                        // Get the Vertex Data Structure for colors
                        var colorDataStruct = colorDataStructs.Count > 0 ? colorDataStructs[0] : null;

                        if (colorDataStruct != null)
                        {
                            // Determine which data block the color data is in
                            // This always seems to be in the second data block
                            switch (colorDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of colors per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var colorOffset = lod.VertexDataOffset + vertexDataOffset + colorDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(colorOffset, SeekOrigin.Begin);

                                var r = br.ReadByte();
                                var g = br.ReadByte();
                                var b = br.ReadByte();
                                var a = br.ReadByte();

                                vertexData.Colors.Add(new SharpDX.Color(r, g, b, a));
                                vertexData.Colors4.Add(new Color4((r / 255f), (g / 255f), (b / 255f), (a / 255f)));
                            }
                        }

                        #endregion

                        #region Color2 Data
                        var color2DataStruct = colorDataStructs.Count > 1 ? colorDataStructs[1] : null;
                        if (color2DataStruct != null)
                        {
                            var dataStruct = color2DataStruct;
                            // Determine which data block the color data is in
                            // This always seems to be in the second data block
                            switch (dataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var dataOffset = lod.VertexDataOffset + vertexDataOffset + dataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(dataOffset, SeekOrigin.Begin);

                                var r = br.ReadByte();
                                var g = br.ReadByte();
                                var b = br.ReadByte();
                                var a = br.ReadByte();

                                vertexData.Colors2.Add(new SharpDX.Color(r, g, b, a));
                            }
                        }
                        #endregion


                        #region TextureCoordinates

                        // Get the Vertex Data Structure for texture coordinates
                        var tcDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                            where vertexDataStruct.DataUsage == VertexUsageType.TextureCoordinate
                                            select vertexDataStruct).FirstOrDefault();

                        if (tcDataStruct != null)
                        {
                            // Determine which data block the texture coordinate data is in
                            // This always seems to be in the second data block
                            switch (tcDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;
                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of texture coordinates per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var tcOffset = lod.VertexDataOffset + vertexDataOffset + tcDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(tcOffset, SeekOrigin.Begin);

                                Vector2 tcVector1;
                                Vector2 tcVector2;
                                // Normal data is either stored in half-floats or singles
                                if (tcDataStruct.DataType == VertexDataType.Half4)
                                {
                                    var x = new SharpDX.Half(br.ReadUInt16());
                                    var y = new SharpDX.Half(br.ReadUInt16());
                                    var x1 = new SharpDX.Half(br.ReadUInt16());
                                    var y1 = new SharpDX.Half(br.ReadUInt16());

                                    tcVector1 = new Vector2(x, y);
                                    tcVector2 = new Vector2(x1, y1);


                                    vertexData.TextureCoordinates0.Add(tcVector1);
                                    vertexData.TextureCoordinates1.Add(tcVector2);
                                }
                                else if (tcDataStruct.DataType == VertexDataType.Half2)
                                {
                                    var x = new SharpDX.Half(br.ReadUInt16());
                                    var y = new SharpDX.Half(br.ReadUInt16());

                                    tcVector1 = new Vector2(x, y);

                                    vertexData.TextureCoordinates0.Add(tcVector1);
                                }
                                else if (tcDataStruct.DataType == VertexDataType.Float2)
                                {
                                    var x = br.ReadSingle();
                                    var y = br.ReadSingle();

                                    tcVector1 = new Vector2(x, y);
                                    vertexData.TextureCoordinates0.Add(tcVector1);
                                }
                                else if (tcDataStruct.DataType == VertexDataType.Float4)
                                {
                                    var x = br.ReadSingle();
                                    var y = br.ReadSingle();
                                    var x1 = br.ReadSingle();
                                    var y1 = br.ReadSingle();

                                    tcVector1 = new Vector2(x, y);
                                    tcVector2 = new Vector2(x1, y1);


                                    vertexData.TextureCoordinates0.Add(tcVector1);
                                    vertexData.TextureCoordinates1.Add(tcVector2);
                                }

                            }
                        }

                        #endregion

                        #region Indices

                        var indexOffset = lod.IndexDataOffset + meshData.MeshInfo.IndexDataOffset * 2;

                        br.BaseStream.Seek(indexOffset, SeekOrigin.Begin);

                        for (var i = 0; i < meshData.MeshInfo.IndexCount; i++)
                        {
                            vertexData.Indices.Add(br.ReadUInt16());
                        }

                        #endregion

                        meshData.VertexData = vertexData;
                        totalMeshNum++;
                    }

                    #region Shape Data Compliation


                    // If the model contains Shape Data, parse the data for each mesh
                    if (xivMdl.HasShapeData && getShapeData)
                    {
                        //Dictionary containing <index data offset, mesh number>
                        var indexMeshNum = new Dictionary<int, int>();

                        var shapeData = xivMdl.MeshShapeData.ShapeDataList;

                        // Get the index data offsets in each mesh
                        for (var i = 0; i < lod.MeshCount; i++)
                        {
                            var indexDataOffset = lod.MeshDataList[i].MeshInfo.IndexDataOffset;

                            if (!indexMeshNum.ContainsKey(indexDataOffset))
                            {
                                indexMeshNum.Add(indexDataOffset, i);
                            }
                        }

                        // For every mesh in this LoD
                        for (var i = 0; i < lod.MeshCount; i++)
                        {
                            var referencePositionsDictionary = new Dictionary<int, Vector3>();
                            var meshShapePositionsDictionary = new SortedDictionary<int, Vector3>();
                            var shapeIndexOffsetDictionary = new Dictionary<int, Dictionary<ushort, ushort>>();

                            // Shape info list
                            var shapeInfoList = xivMdl.MeshShapeData.ShapeInfoList;

                            // Total number of Shapes
                            var totalShapes = xivMdl.ModelData.ShapeCount;

                            for (var j = 0; j < totalShapes; j++)
                            {
                                var shapeInfo = shapeInfoList[j];

                                var indexPart = shapeInfo.ShapeLods[lodNum];

                                // The part count
                                var infoPartCount = indexPart.PartCount;

                                for (var k = 0; k < infoPartCount; k++)
                                {
                                    // Gets the data info for the part
                                    var shapeDataInfo = xivMdl.MeshShapeData.ShapeParts[indexPart.PartOffset + k];

                                    // The offset in the shape data 
                                    var indexDataOffset = shapeDataInfo.MeshIndexOffset;

                                    var indexMeshLocation = 0;

                                    // Determine which mesh the info belongs to
                                    if (indexMeshNum.ContainsKey(indexDataOffset))
                                    {
                                        indexMeshLocation = indexMeshNum[indexDataOffset];

                                        // Move to the next part if it is not the current mesh
                                        if (indexMeshLocation != i)
                                        {
                                            continue;
                                        }
                                    }

                                    // Get the mesh data
                                    var mesh = lod.MeshDataList[indexMeshLocation];

                                    // Get the shape data for the current mesh
                                    var shapeDataForMesh = shapeData.GetRange(shapeDataInfo.ShapeDataOffset, shapeDataInfo.IndexCount);

                                    // Fill shape data dictionaries
                                    ushort dataIndex = ushort.MaxValue;
                                    foreach (var data in shapeDataForMesh)
                                    {
                                        var referenceIndex = 0;

                                        if (data.BaseIndex < mesh.VertexData.Indices.Count)
                                        {
                                            // Gets the index to which the data is referencing
                                            referenceIndex = mesh.VertexData.Indices[data.BaseIndex];

                                            //throw new Exception($"Reference Index is larger than the index count. Reference Index: {data.ReferenceIndexOffset}  Index Count: {mesh.VertexData.Indices.Count}");
                                        }

                                        if (!referencePositionsDictionary.ContainsKey(data.BaseIndex))
                                        {
                                            if (mesh.VertexData.Positions.Count > referenceIndex)
                                            {
                                                referencePositionsDictionary.Add(data.BaseIndex, mesh.VertexData.Positions[referenceIndex]);
                                            }
                                        }

                                        if (!meshShapePositionsDictionary.ContainsKey(data.ShapeVertex))
                                        {
                                            if (data.ShapeVertex >= mesh.VertexData.Positions.Count)
                                            {
                                                meshShapePositionsDictionary.Add(data.ShapeVertex, new Vector3(0));
                                            }
                                            else
                                            {
                                                meshShapePositionsDictionary.Add(data.ShapeVertex, mesh.VertexData.Positions[data.ShapeVertex]);
                                            }
                                        }
                                    }

                                    if (mesh.ShapePathList != null)
                                    {
                                        mesh.ShapePathList.Add(shapeInfo.Name);
                                    }
                                    else
                                    {
                                        mesh.ShapePathList = new List<string> { shapeInfo.Name };
                                    }
                                }
                            }
                        }
                    }

                    lodNum++;

                    #endregion
                }
            }

            return xivMdl;
        }


        /// <summary>
        /// Extracts and calculates the full MTRL paths from a given MDL file.
        /// A material variant of -1 gets the materials for ALL variants,
        /// effectively generating the 'child files' list for an Mdl file.
        /// </summary>
        /// <param name="mdlPath"></param>
        /// <param name="getOriginal"></param>
        /// <returns></returns>
        public async Task<List<string>> GetReferencedMaterialPaths(string mdlPath, int materialVariant = -1, bool getOriginal = false, bool includeSkin = true, IndexFile index = null, ModList modlist = null)
        {
            // Language is irrelevant here.
            var dataFile = IOUtil.GetDataFileFromPath(mdlPath);
            var _mtrl = new Mtrl(XivCache.GameInfo.GameDirectory);
            var _imc = new Imc(_gameDirectory);
            var useCached = true;
            if (index == null)
            {
                useCached = false;
                var _index = new Index(_gameDirectory);
                var _modding = new Modding(_gameDirectory);
                index = await _index.GetIndexFile(dataFile, false, true);
                modlist = await _modding.GetModListAsync();
            }

            var materials = new List<string>();

            // Read the raw Material names from the file.
            var materialNames = await GetReferencedMaterialNames(mdlPath, getOriginal, index, modlist);
            var root = await XivCache.GetFirstRoot(mdlPath);
            if(materialNames.Count == 0)
            {
                return materials;
            }

            var materialVariants = new HashSet<int>();
            if (materialVariant >= 0)
            {
                // If we had a specific variant to get, just use that.
                materialVariants.Add(materialVariant);

            }
            else if(useCached && root != null)
            {
                var metadata = await ItemMetadata.GetFromCachedIndex(root, index);
                if (metadata.ImcEntries.Count == 0 || !Imc.UsesImc(root))
                {
                    materialVariants.Add(1);
                }
                else
                {
                    foreach (var entry in metadata.ImcEntries)
                    {
                        materialVariants.Add(entry.MaterialSet);
                    }
                }
            }
            else
            {

                // Otherwise, we have to resolve all possible variants.
                var imcPath = ItemType.GetIMCPathFromChildPath(mdlPath);
                if (imcPath == null)
                {
                    // No IMC file means this Mdl doesn't use variants/only has a single variant.
                    materialVariants.Add(1);
                }
                else
                {

                    // We need to get the IMC info for this MDL so that we can pull every possible Material Variant.
                    try
                    {
                        var info = await _imc.GetFullImcInfo(imcPath, index, modlist);
                        var slotRegex = new Regex("_([a-z]{3}).mdl$");
                        var slot = "";
                        var m = slotRegex.Match(mdlPath);
                        if (m.Success)
                        {
                            slot = m.Groups[1].Value;
                        }

                        // We have to get all of the material variants used for this item now.
                        var imcInfos = info.GetAllEntries(slot, true);
                        foreach (var i in imcInfos)
                        {
                            if (i.MaterialSet != 0)
                            {
                                materialVariants.Add(i.MaterialSet);
                            }
                        }
                    }
                    catch
                    {
                        // Some Dual Wield weapons don't have any IMC entry at all.
                        // In these cases they just use Material Variant 1 (Which is usually a simple dummy material)
                        materialVariants.Add(1);
                    }
                }
            }

            // We have to get every material file that this MDL references.
            // That means every variant of every material referenced.
            var uniqueMaterialPaths = new HashSet<string>();
            foreach (var mVariant in materialVariants)
            {
                foreach (var mName in materialNames)
                {
                    // Material ID 0 is SE's way of saying it doesn't exist.
                    if (mVariant != 0)
                    {
                        var path = _mtrl.GetMtrlPath(mdlPath, mName, mVariant);
                        uniqueMaterialPaths.Add(path);
                    }
                }
            }

            if(!includeSkin)
            {
                var skinRegex = new Regex("chara/human/c[0-9]{4}/obj/body/b[0-9]{4}/material/v[0-9]{4}/.+\\.mtrl");
                var toRemove = new List<string>();
                foreach(var mtrl in uniqueMaterialPaths)
                {
                    if(skinRegex.IsMatch(mtrl))
                    {
                        toRemove.Add(mtrl);
                    }
                }

                foreach(var mtrl in toRemove)
                {
                    uniqueMaterialPaths.Remove(mtrl);
                }
            }

            return uniqueMaterialPaths.ToList();
        }



        /// <summary>
        /// Extracts just the MTRL names from a mdl file.
        /// </summary>
        /// <param name="mdlPath"></param>
        /// <param name="getOriginal"></param>
        /// <returns></returns>
        public async Task<List<string>> GetReferencedMaterialNames(string mdlPath, bool getOriginal = false, IndexFile index = null, ModList modlist = null)
        {
            var materials = new List<string>();
            var dat = new Dat(_gameDirectory);
            var modding = new Modding(_gameDirectory);


            if (index == null)
            {
                var _index = new Index(_gameDirectory);
                index = await _index.GetIndexFile(IOUtil.GetDataFileFromPath(mdlPath), false, true);
                
            }

            var offset = index.Get8xDataOffset(mdlPath);
            if (getOriginal)
            {
                if(modlist == null)
                {
                    modlist = await modding.GetModListAsync();
                }

                var mod = modlist.Mods.FirstOrDefault(x => x.fullPath == mdlPath);
                if(mod != null)
                {
                    offset = mod.data.originalOffset;
                }
            }

            if (offset == 0)
            {
                throw new Exception($"Could not find offset for {mdlPath}");
            }

            var mdlData = await dat.GetType3Data(offset, IOUtil.GetDataFileFromPath(mdlPath));


            using (var br = new BinaryReader(new MemoryStream(mdlData.Data)))
            {
                // We skip the Vertex Data Structures for now
                // This is done so that we can get the correct number of meshes per LoD first
                br.BaseStream.Seek(64 + 136 * mdlData.MeshCount + 4, SeekOrigin.Begin);

                var PathCount = br.ReadInt32();
                var PathBlockSize = br.ReadInt32();


                Regex materialRegex = new Regex(".*\\.mtrl$");

                for (var i = 0; i < PathCount; i++)
                {
                    byte a;
                    List<byte> bytes = new List<byte>(); ;
                    while ((a = br.ReadByte()) != 0)
                    {
                        bytes.Add(a);
                    }

                    var st = Encoding.ASCII.GetString(bytes.ToArray()).Replace("\0", "");

                    if (materialRegex.IsMatch(st))
                    {
                        materials.Add(st);
                    }
                }

            }
            return materials;
        }


        /// <summary>
        /// Retreieves the available list of file extensions the framework has importers available for.
        /// </summary>
        /// <returns></returns>
        public List<string> GetAvailableImporters()
        {
            var cwd = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            cwd = cwd.Replace("\\", "/");
            string importerPath = cwd + "/converters/";
            var ret = new List<string>();
            ret.Add("db");  // Raw already-parsed DB files are fine.

            var directories = Directory.GetDirectories(importerPath);
            foreach (var d in directories)
            {
                var suffix = (d.Replace(importerPath, "")).ToLower();
                if (ret.IndexOf(suffix) < 0)
                {
                    ret.Add(suffix);
                }
            }
            return ret;
        }

        /// <summary>
        /// Retreieves the available list of file extensions the framework has exporters available for.
        /// </summary>
        /// <returns></returns>
        public List<string> GetAvailableExporters()
        {
            var cwd = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            cwd = cwd.Replace("\\", "/");
            string importerPath = cwd + "/converters/";
            var ret = new List<string>();
            ret.Add("obj"); // OBJ handler is internal.
            ret.Add("db");  // Raw already-parsed DB files are fine.

            var directories = Directory.GetDirectories(importerPath);
            foreach (var d in directories)
            {
                var suffix = (d.Replace(importerPath, "")).ToLower();
                if (ret.IndexOf(suffix) < 0)
                {
                    ret.Add(suffix);
                }
            }
            return ret;
        }

        // Just a default no-op function if we don't care about warning messages.
        private void NoOp(bool isWarning, string message)
        {
            //No-Op.
        }


        private async Task<string> RunExternalImporter(string importerName, string filePath, Action<bool, string> loggingFunction = null)
        {

            var cwd = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            string importerFolder = cwd + "\\converters\\" + importerName;
            if (loggingFunction == null)
            {
                loggingFunction = NoOp;
            }

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = importerFolder + "\\converter.exe",
                    Arguments = "\"" + filePath + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WorkingDirectory = "" + importerFolder + "",
                    CreateNoWindow = true
                }
            };

            // Pipe the process output to our logging function.
            proc.OutputDataReceived += (object sender, DataReceivedEventArgs e) =>
            {
                loggingFunction(false, e.Data);
            };

            // Pipe the process output to our logging function.
            proc.ErrorDataReceived += (object sender, DataReceivedEventArgs e) =>
            {
                loggingFunction(true, e.Data);
            };

            proc.EnableRaisingEvents = true;

            loggingFunction(false, "Starting " + importerName.ToUpper() + " Importer...");
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            int? code = null;
            proc.Exited += (object sender, EventArgs e) =>
            {
                code = proc.ExitCode;
            };

            return await Task.Run(async () =>
            {
                while (code == null)
                {
                    Thread.Sleep(100);
                }

                if (code != 0)
                {
                    throw (new Exception("Importer exited with error code: " + proc.ExitCode.ToString()));
                }
                return importerFolder + "\\result.db";
            });
        }

        /// <summary>
        /// Import a given model
        /// </summary>
        /// <param name="item">The current item being imported</param>
        /// <param name="race">The racial model to replace of the item</param>
        /// <param name="path">The location of the file to import</param>
        /// <param name="source">The source/application that is writing to the dat.</param>
        /// <param name="intermediaryFunction">Function to call after populating the TTModel but before converting it to a Mdl.
        ///     Takes in the new TTModel we're importing, and the old model we're overwriting.
        ///     Should return a boolean indicating whether the process should continue or cancel (false to cancel)
        /// </param>
        /// <param name="loggingFunction">
        /// Function to call when the importer receives a new log line.
        /// Takes in [bool isWarning, string message].
        /// </param>
        /// <param name="rawDataOnly">If this function should not actually finish the import and only return the raw byte data.</param>
        /// <returns>A dictionary containing any warnings encountered during import.</returns>
        public async Task ImportModel(IItemModel item, XivRace race, string path, ModelModifierOptions options = null, Action<bool, string> loggingFunction = null, Func<TTModel, TTModel, Task<bool>> intermediaryFunction = null, string source = "Unknown", string submeshId = null, bool rawDataOnly = false)
        {

            #region Setup and Validation
            if (options == null)
            {
                options = new ModelModifierOptions();
            }

            if (loggingFunction == null)
            {
                loggingFunction = NoOp;
            }

            // Test the Path.
            if (path != null && path != "")
            {
                DirectoryInfo fileLocation = null;
                try
                {
                    fileLocation = new DirectoryInfo(path);
                }
                catch (Exception ex)
                {
                    throw new IOException("Invalid file path.");
                }

                if (!File.Exists(fileLocation.FullName))
                {
                    throw new IOException("The file provided for import does not exist");
                }
            }
            #endregion

            var modding = new Modding(_gameDirectory);
            var dat = new Dat(_gameDirectory);
            var mdlPath = await GetMdlPath(item, race);

            var bytes = await FileToModelBytes(path, mdlPath, options, loggingFunction, intermediaryFunction, submeshId);
            var compressed = await CompressMdlFile(bytes);
            _rawData = compressed;

            var modEntry = await modding.TryGetModEntry(mdlPath);
            if (!rawDataOnly)
            {
                loggingFunction(false, "Writing MDL File to FFXIV File System...");
                await dat.WriteModFile(compressed, mdlPath, source, item);
            }

            loggingFunction(false, "Job done!");
            return;
        }

        /// <summary>
        /// Takes an external FBX/DB file and converts it into an uncompressed MDL file.
        /// Due to the nature of the external importers, it is required that the file exists on disk
        /// and not just a byte array/stream.
        /// </summary>
        /// <param name="externalPath"></param>
        /// <param name="internalPath"></param>
        /// <param name="options"></param>
        /// <param name="loggingFunction"></param>
        /// <param name="intermediaryFunction"></param>
        /// <param name="submeshId"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        /// <exception cref="InvalidDataException"></exception>
        public async Task<byte[]> FileToModelBytes(string externalPath, string internalPath, ModelModifierOptions options = null, Action<bool, string> loggingFunction = null, Func<TTModel, TTModel, Task<bool>> intermediaryFunction = null, string submeshId = null)
        {

            if (options == null)
            {
                options = new ModelModifierOptions();
            }

            if (loggingFunction == null)
            {
                loggingFunction = NoOp;
            }

            var modding = new Modding(_gameDirectory);
            var dat = new Dat(_gameDirectory);
            var mod = await modding.TryGetModEntry(internalPath);

            // Resolve the current (and possibly modded) Mdl.
            XivMdl currentMdl = null;
            XivMdl originalMdl = null;
            try
            {
                // Load the base game model.
                // This is used for some model modifier options and the like.
                // Some paths may not actually use this(?)
                originalMdl = await GetRawMdlData(internalPath, true);
            }
            catch
            {
                throw new Exception("Unable to load base MDL file.");
            }

            try
            {
                if (mod != null)
                {
                    // If we have a modded base, we need to load that as well.
                    currentMdl = await GetRawMdlData(internalPath);
                } else
                {
                    currentMdl = originalMdl;
                }
            }
            catch (Exception ex)
            {
                loggingFunction(true, "Unable to load current MDL file.  Using base game MDL file...");
                currentMdl = originalMdl;
            }

            byte[] bytes = null;

            // Wrapping this in an await ensures we're run asynchronously on a new thread.
            await Task.Run(async () =>
            {
                var filePath = currentMdl.MdlPath;

                #region TTModel Loading
                // Probably could stand to push this out to its own function later.
                var mdlPath = currentMdl.MdlPath;

                loggingFunction = loggingFunction == null ? NoOp : loggingFunction;
                loggingFunction(false, "Starting Import of file: " + externalPath);

                var suffix = externalPath == null || externalPath == "" ? null : Path.GetExtension(externalPath).ToLower().Substring(1);
                TTModel ttModel = null;


                // Loading and Running the actual Importers.
                if (externalPath == null || externalPath == "")
                {
                    // If we were given no path, load the current model.
                    ttModel = await GetModel(internalPath);
                }
                else if (suffix == "db")
                {
                    // Raw already converted DB file, just load it.
                    loggingFunction(false, "Loading intermediate file...");
                    ttModel = TTModel.LoadFromFile(externalPath, loggingFunction);
                }
                else
                {
                    // External Importer converts the file to .db format.
                    var dbFile = await RunExternalImporter(suffix, externalPath, loggingFunction);
                    loggingFunction(false, "Loading intermediate file...");
                    ttModel = TTModel.LoadFromFile(dbFile, loggingFunction);
                }
                #endregion


                var sane = TTModel.SanityCheck(ttModel, loggingFunction);
                if (!sane)
                {
                    throw new InvalidDataException("Model is corrupt or otherwise invalid.");
                }



                // At this point we now have a fully populated TTModel entry.
                // Time to pull in the Model Modifier for any extra steps before we pass
                // it to the raw MDL creation function.
                loggingFunction(false, "Merging in existing Attribute & Material Data...");

                // Apply our Model Modifier options to the model.
                options.Apply(ttModel, currentMdl, originalMdl, loggingFunction);


                // Call the user function, if one was provided.
                if (intermediaryFunction != null)
                {
                    loggingFunction(false, "Waiting on user...");

                    // Bool says whether or not we should continue.
                    var oldModel = TTModel.FromRaw(originalMdl);
                    bool cont = await intermediaryFunction(ttModel, oldModel);
                    if (!cont)
                    {
                        loggingFunction(false, "User cancelled import process.");
                        // This feels really dumb to cancel this via a throw, but we have no other method to do so...?
                        throw new Exception("cancel");
                    }
                }

                // Fix up the skin references, just because we can/it helps user expectation.
                // Doesn't really matter as these get auto-resolved in game no matter what race they point to.
                ModelModifiers.FixUpSkinReferences(ttModel, filePath, loggingFunction);

                // Check for common user errors.
                TTModel.CheckCommonUserErrors(ttModel, loggingFunction);

                // Time to create the raw MDL.
                loggingFunction(false, "Creating MDL file from processed data...");
                bytes = await MakeMdlFile(ttModel, currentMdl, loggingFunction);

                loggingFunction(false, "Job done!");
            });

            return bytes;
        }

        public async Task<byte[]> MakeCompressedMdlFile(TTModel ttModel, XivMdl ogMdl, Action<bool, string> loggingFunction = null)
        {
            var mdl = await MakeMdlFile(ttModel, ogMdl, loggingFunction);
            var compressed = await CompressMdlFile(mdl);
            return compressed;
        }

        /// <summary>
        /// Creates a new Uncompressed MDL file from the given information.
        /// OGMdl is used to fill in gaps in data types we do not know about.
        /// TODO: It should be possible at this point to adjust this function to accomodate [null] ogMDLs.
        /// </summary>
        /// <param name="ttModel">The ttModel to import</param>
        /// <param name="ogMdl">The currently modified Mdl file.</param>
        public async Task<byte[]> MakeMdlFile(TTModel ttModel, XivMdl ogMdl, Action<bool, string> loggingFunction = null)
        {
            var mdlVersion = ttModel.MdlVersion > 0 ? ttModel.MdlVersion : ogMdl.MdlVersion;
            ttModel.MdlVersion = mdlVersion;

            byte _LoDCount = 1;

            // Pipe some user var down here and we could ship this toggle.
            // Not really much reason to ever use lower precision other than file size/perf though.
            bool _UpgradePrecision = true;

            // Arbitrarily long distance that we use for LoD distance.
            // If this is too high the game explodes. 500 Meters seems ok.  1000 Crashes.
            // Maybe it's at some point capped to 512? /Random Guess.
            float _ReallyLongDistance = 500.0f;

            // Mildly long distance used for Texture LoD levels.
            // The default is around 50 meters.  We bump it up to about double.
            float _KindOfLongDistance = 100.0f;

            if (loggingFunction == null)
            {
                loggingFunction = NoOp;
            }

            try
            { 
                var rawShapeData = ttModel.GetRawShapeParts();

                // Calculate Radius here for convenience.
                // These values also used in writing bounding boxes later.
                float minX = 9999.0f, minY = 9999.0f, minZ = 9999.0f;
                float maxX = -9999.0f, maxY = -9999.0f, maxZ = -9999.0f;
                float absX = 0, absY = 0, absZ = 0;
                foreach (var m in ttModel.MeshGroups)
                {
                    foreach (var p in m.Parts)
                    {
                        foreach (var v in p.Vertices)
                        {
                            minX = minX < v.Position.X ? minX : v.Position.X;
                            minY = minY < v.Position.Y ? minY : v.Position.Y;
                            minZ = minZ < v.Position.Z ? minZ : v.Position.Z;

                            maxX = maxX > v.Position.X ? maxX : v.Position.X;
                            maxY = maxY > v.Position.Y ? maxY : v.Position.Y;
                            maxZ = maxZ > v.Position.Z ? maxZ : v.Position.Z;

                            absX = absX < Math.Abs(v.Position.X) ? Math.Abs(v.Position.X) : absX;
                            absY = absY < Math.Abs(v.Position.Y) ? Math.Abs(v.Position.Y) : absY;
                            absZ = absZ < Math.Abs(v.Position.Z) ? Math.Abs(v.Position.Z) : absZ;
                        }
                    }
                }
                var minVect = new Vector3(minX, minY, minZ);
                var maxVect = new Vector3(maxX, maxY, maxZ);

                // Radius seems like it's from (0,0,0), so just take the maximum absolute distances from 0 on each axis, and call it good enough.
                var absVect = new Vector3(absX, absY, absZ);
                var modelRadius = absVect.Length();

                // Vertex Info
                #region Vertex Info Block

                var vertexInfoBlock = new List<byte>();
                var vertexInfoLists = new List<Dictionary<VertexUsageType, List<VertexDataType>>>();
                var vertexStreamCounts = new List<int>();

                var perVertexDataSizes = new List<List<int>>();

                for (var meshNum = 0; meshNum < ttModel.MeshGroups.Count; meshNum++)
                {
                    // We only care about LoD 0.
                    var lod = ogMdl.LoDList[0];

                    var vdsDictionary = new Dictionary<VertexUsageType, List<VertexDataType>>();
                    var startOfMesh = vertexInfoBlock.Count;

                    // Test if we have both old and new data or not.
                    var ogGroup = lod.MeshDataList.Count > meshNum ? lod.MeshDataList[meshNum] : null;
                    var ttMeshGroup = ttModel.MeshGroups.Count > meshNum ? ttModel.MeshGroups[meshNum] : null;


                    List<VertexDataStruct> source;
                    if (ogGroup == null)
                    {
                        // New Group, copy data over.
                        source = lod.MeshDataList[0].VertexDataStructList;
                    }
                    else
                    {
                        source = ogGroup.VertexDataStructList;
                    }

                    // Vertex Header Writing
                    var runningOffsets = new List<int>() { 0, 0, 0 };
                    source.OrderBy(x => (x.DataBlock * -1000) + x.DataOffset);
                    vertexStreamCounts.Add(source.Max(x => x.DataBlock) + 1);

                    // If we're upgrading precision on a v6 mdl, might as well add all the bells and whistles.
                    if(mdlVersion >= 6 && _UpgradePrecision)
                    {
                        // Add precomputed tangent data.
                        var tangentCount = source.Count(x => x.DataUsage == VertexUsageType.Tangent);
                        var binormalIdx = source.FindIndex(x => x.DataUsage == VertexUsageType.Binormal);
                        if(binormalIdx >= 0 && tangentCount <= 0 && false)
                        {
                            // Goes in after Binormal
                            source.Insert(binormalIdx + 1, new VertexDataStruct()
                            {
                                DataBlock = 1,
                                DataOffset = 0, // Offset doesn't matter since we recalculate it anyways.
                                DataType = VertexDataType.Ubyte4n,
                                DataUsage = VertexUsageType.Tangent
                            });

                        }
                        
                        // Add 2nd color channel for faux-wind simulation.
                        var colorCounts = source.Count(x => x.DataUsage == VertexUsageType.Color);
                        var colorIdx = source.FindIndex(x => x.DataUsage == VertexUsageType.Color);
                        if (colorCounts == 1)
                        {
                            source.Insert(colorIdx + 1, new VertexDataStruct()
                            {
                                DataBlock = 1,
                                DataOffset = 0, // Offset doesn't matter since we recalculate it anyways.
                                DataType = VertexDataType.Ubyte4n,
                                DataUsage = VertexUsageType.Color
                            });
                        }
                    }

                    foreach (var vds in source)
                    {
                        var dataBlock = vds.DataBlock;
                        var dataOffset = vds.DataOffset;
                        var dataType = vds.DataType;
                        var dataUsage = vds.DataUsage;

                        // Model version 5 doesn't allow double color channel information.
                        if (vdsDictionary.ContainsKey(dataUsage) && mdlVersion == 5)
                        {
                            continue;
                        }

                        // Perform precision updates if requested, and adjustments for MDL version.
                        if (dataUsage == VertexUsageType.Position)
                        {
                            if (_UpgradePrecision)
                            {
                                dataType = VertexDataType.Float3;
                            }
                            else
                            {
                                dataType = VertexDataType.Half4;
                            }
                        }

                        if (dataUsage == VertexUsageType.BoneWeight)
                        {
                            if (mdlVersion >= 6 && _UpgradePrecision)
                            {
                                dataType = VertexDataType.UByte8;
                            }
                            else
                            {
                                dataType = VertexDataType.Ubyte4n;
                            }
                        }

                        if (dataUsage == VertexUsageType.BoneIndex)
                        {
                            if (mdlVersion >= 6 && _UpgradePrecision)
                            {
                                dataType = VertexDataType.UByte8;
                            }
                            else
                            {
                                dataType = VertexDataType.Ubyte4;
                            }
                        }

                        if (dataUsage == VertexUsageType.Normal)
                        {
                            if (_UpgradePrecision)
                            {
                                dataType = VertexDataType.Float3;
                            }
                            else
                            {
                                dataType = VertexDataType.Half4;
                            }
                        }

                        if (dataUsage == VertexUsageType.TextureCoordinate)
                        {
                            if (_UpgradePrecision)
                            {
                                if (dataType == VertexDataType.Half2)
                                {
                                    dataType = VertexDataType.Float2;
                                }
                                else if (dataType == VertexDataType.Half4)
                                {
                                    dataType = VertexDataType.Float4;
                                }
                            }
                            else
                            {
                                if (dataType == VertexDataType.Float2)
                                {
                                    dataType = VertexDataType.Half2;
                                }
                                else if (dataType == VertexDataType.Float4)
                                {
                                    dataType = VertexDataType.Half4;
                                }
                            }
                        }

                        var count = 0;
                        if (!vdsDictionary.ContainsKey(dataUsage))
                        {
                            vdsDictionary.Add(dataUsage, new List<VertexDataType>());
                        }
                        else
                        {
                            count = vdsDictionary[dataUsage].Count;
                        }
                        vdsDictionary[dataUsage].Add(dataType);

                        vertexInfoBlock.Add((byte)dataBlock);
                        vertexInfoBlock.Add((byte)runningOffsets[dataBlock]);
                        vertexInfoBlock.Add((byte)dataType);
                        vertexInfoBlock.Add((byte)dataUsage);
                        vertexInfoBlock.Add((byte)count);

                        var size = VertexDataTypeInfo.Sizes[dataType];
                        runningOffsets[dataBlock] += size;


                        // Padding between usage blocks
                        vertexInfoBlock.AddRange(new byte[3]);
                    }

                    // Store these for later.
                    perVertexDataSizes.Add(runningOffsets);

                    // End flag
                    vertexInfoBlock.Add(0xFF);
                    var meshBlockSize = vertexInfoBlock.Count - startOfMesh;

                    // Fill rest of the array size.
                    if (meshBlockSize < _vertexDataHeaderSize)
                    {
                        var remaining = _vertexDataHeaderSize - meshBlockSize;

                        vertexInfoBlock.AddRange(new byte[remaining]);
                    }
                    var finalMeshBlockSize = vertexInfoBlock.Count - startOfMesh;

                    // Add this mesh group's vertex dictionary.
                    vertexInfoLists.Add(vdsDictionary);
                }
                #endregion


                #region Geometry Data Blocks
                // We can calculate these as soon as we know the data types we're writing.
                // Though the data will eventually go at the very end of the MDL file.

                // Get the geometry data we want to import.
                var geometryData = GetBasicGeometryData(ttModel, vertexInfoLists, loggingFunction);

                // The above does not include shade data vertices/indices, which must be written after.
                if (ttModel.HasShapeData)
                {
                    // Shape parts need to be rewitten in specific order.
                    var shapeVertexLists = rawShapeData.Vertices;
                    for(int i = 0; i < shapeVertexLists.Count; i++)
                    {
                        var meshGeometryData = geometryData[i];
                        var vertexList = shapeVertexLists[i];
                        var vertexInfo = vertexInfoLists[i];
                        foreach (var v in vertexList)
                        {
                            WriteVertex(meshGeometryData, vertexInfo, ttModel, v, loggingFunction);
                        }
                    }
                }

                var vertexDataBlock = new List<byte>();
                var indexDataBlock = new List<byte>();

                // This is keyed by Mesh Id => Vertex Stream #
                List<List<int>> meshVertexOffsets = new List<List<int>>();
                List<int> meshIndexOffsets = new List<int>();
                for (var i = 0; i < ttModel.MeshGroups.Count; i++)
                {
                    var importData = geometryData[i];
                    var vertexBlockOffsets = new List<int>();
                    meshVertexOffsets.Add(vertexBlockOffsets);

                    vertexBlockOffsets.Add(vertexDataBlock.Count);
                    vertexDataBlock.AddRange(importData.VertexData0);

                    vertexBlockOffsets.Add(vertexDataBlock.Count);
                    vertexDataBlock.AddRange(importData.VertexData1);

                    vertexBlockOffsets.Add(vertexDataBlock.Count);
                    vertexDataBlock.AddRange(importData.VertexData2);

                    meshIndexOffsets.Add(indexDataBlock.Count / 2);
                    indexDataBlock.AddRange(importData.IndexData);

                    Dat.Pad(indexDataBlock, 16);
                }
                #endregion


                // Path Data
                #region Path Info Block

                var pathInfoBlock = new List<byte>();
                var pathCount = 0;

                // Attribute paths
                var attributeOffsetList = new List<int>();

                var attributes = ttModel.Attributes;
                foreach (var atr in attributes)
                {
                    // Attribute offset in path data block
                    attributeOffsetList.Add(pathInfoBlock.Count);

                    // Path converted to bytes
                    pathInfoBlock.AddRange(Encoding.UTF8.GetBytes(atr));

                    // Byte between paths
                    pathInfoBlock.Add(0);
                    pathCount++;
                }

                // Bone paths
                var boneOffsetList = new List<int>();
                var boneStrings = new List<string>();

                // Write the full model level list of bones.
                foreach (var bone in ttModel.Bones)
                {
                    // Bone offset in path data block
                    boneOffsetList.Add(pathInfoBlock.Count);

                    // Path converted to bytes
                    pathInfoBlock.AddRange(Encoding.UTF8.GetBytes(bone));
                    boneStrings.Add(bone);

                    // Byte between paths
                    pathInfoBlock.Add(0);
                    pathCount++;
                }

                // Material paths
                var materialOffsetList = new List<int>();
                foreach (var material in ttModel.Materials)
                {
                    // Material offset in path data block
                    materialOffsetList.Add(pathInfoBlock.Count);

                    // Path converted to bytes
                    pathInfoBlock.AddRange(Encoding.UTF8.GetBytes(material));

                    // Byte between paths
                    pathInfoBlock.Add(0);
                    pathCount++;
                }

                // Shape paths
                var shapeOffsetList = new List<int>();
                if (ttModel.HasShapeData)
                {
                    foreach (var shape in ttModel.ShapeNames)
                    {
                        // Shape offset in path data block
                        shapeOffsetList.Add(pathInfoBlock.Count);

                        // Path converted to bytes
                        pathInfoBlock.AddRange(Encoding.UTF8.GetBytes(shape));

                        // Byte between paths
                        pathInfoBlock.Add(0);
                        pathCount++;
                    }
                }

                // Extra paths
                var extraStringsOffsetList = new List<int>();
                foreach (var extra in ogMdl.PathData.ExtraPathList)
                {
                    // Shape offset in path data block
                    extraStringsOffsetList.Add(pathInfoBlock.Count);

                    // Path converted to bytes
                    pathInfoBlock.AddRange(Encoding.UTF8.GetBytes(extra));

                    // Byte between paths
                    pathInfoBlock.Add(0);
                    pathCount++;
                }

                // Pad out to divisions of 4 bytes.
                Dat.Pad(pathInfoBlock, 4);

                var pathHeader = new List<byte>();
                pathHeader.AddRange(BitConverter.GetBytes(pathCount));
                pathHeader.AddRange(BitConverter.GetBytes(pathInfoBlock.Count));
                pathInfoBlock.InsertRange(0, pathHeader.ToArray());
                #endregion

                // Model Data
                #region Basic Model Data Block

                var basicModelBlock = new List<byte>();

                var ogModelData = ogMdl.ModelData;

                short meshCount = (short)(ttModel.MeshGroups.Count + ogMdl.LoDList[0].WaterMeshCount);
                short higherLodMeshCount = 0;
                meshCount += higherLodMeshCount;
                ogModelData.MeshCount = meshCount;
                // Recalculate total number of parts.
                short meshPartCount  = 0;
                foreach (var m in ttModel.MeshGroups)
                {
                    meshPartCount += (short)m.Parts.Count;
                }

                // Geometry-related stuff that we have actual values for.
                basicModelBlock.AddRange(BitConverter.GetBytes(modelRadius));
                basicModelBlock.AddRange(BitConverter.GetBytes(meshCount));
                basicModelBlock.AddRange(BitConverter.GetBytes((short)ttModel.Attributes.Count));
                basicModelBlock.AddRange(BitConverter.GetBytes(meshPartCount));
                basicModelBlock.AddRange(BitConverter.GetBytes((short)ttModel.Materials.Count));
                basicModelBlock.AddRange(BitConverter.GetBytes((short)ttModel.Bones.Count));
                basicModelBlock.AddRange(BitConverter.GetBytes((short)ttModel.MeshGroups.Count)); // Bone List Count is 1x # of groups for us.
                basicModelBlock.AddRange(BitConverter.GetBytes(ttModel.HasShapeData ? (short)ttModel.ShapeNames.Count : (short)0));
                basicModelBlock.AddRange(BitConverter.GetBytes(ttModel.HasShapeData ? (short)ttModel.ShapePartCount : (short)0));
                basicModelBlock.AddRange(BitConverter.GetBytes(ttModel.HasShapeData ? (ushort)ttModel.ShapeDataCount : (ushort)0));
                basicModelBlock.Add(_LoDCount); // LoD count, set to 1 since we only use the highest LoD


                // Largely unknown stuff.  Flag definitions have comments for their values, but typically are just 0 on most normal things.
                basicModelBlock.Add(ogModelData.Flags1);
                basicModelBlock.AddRange(BitConverter.GetBytes(ogModelData.ElementIdCount));
                basicModelBlock.Add(ogModelData.TerrainShadowMeshCount);
                basicModelBlock.Add(ogModelData.Flags2);
                
                // Model and Shadow Clip-Out distances.  Can set these to 0 to disable.
                basicModelBlock.AddRange(BitConverter.GetBytes(0));
                basicModelBlock.AddRange(BitConverter.GetBytes(0));

                // Largely unknown stuff.
                basicModelBlock.AddRange(BitConverter.GetBytes(ogModelData.UnknownBoundingBoxCount));
                basicModelBlock.AddRange(BitConverter.GetBytes(ogModelData.TerrainShadowSubmeshCount));
                basicModelBlock.Add(ogModelData.Unknown10a);
                
                // Handles which materials get some variable data (Ex. FC crests?)
                basicModelBlock.Add(ogModelData.BgChangeMaterialIndex);
                basicModelBlock.Add(ogModelData.BgCrestChangeMaterialIndex);

                // More Unknowns
                basicModelBlock.Add(ogModelData.Unknown12);

                // We fix this pointer later after bone table is done.
                var boneSetSizePointer = basicModelBlock.Count;
                basicModelBlock.AddRange(BitConverter.GetBytes((short)0));

                // Unknowns that are probably partly padding.
                basicModelBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown13));
                basicModelBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown14));
                basicModelBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown15));
                basicModelBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown16));
                basicModelBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown17));



                #endregion

                // Unknown Data 0
                #region Unknown Data Block 0

                var unknownDataBlock0 = ogMdl.UnkData0.Unknown;



                #endregion


                // Extra LoD Data
                #region Extra Level Of Detail Block
                var extraLodDataBlock = new List<byte>();

                // This seems to mostly be used in furniture, but some other things
                // use it too.  Perchberd has great info on this stuff.
                if (ogMdl.ExtraLoDList != null && ogMdl.ExtraLoDList.Count > 0)
                {
                    foreach (var ld in ogMdl.ExtraLoDList)
                    {
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.MeshOffset));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.MeshCount));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.ModelLoDRange));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.TextureLoDRange));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.WaterMeshIndex));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.WaterMeshCount));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.ShadowMeshIndex));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.ShadowMeshCount));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.TerrainShadowMeshIndex));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.TerrainShadowMeshCount));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.FogMeshIndex));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.FogMeshCount));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.EdgeGeometrySize));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.EdgeGeometryOffset));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.Unknown6));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.Unknown7));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.VertexDataSize));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.IndexDataSize));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.VertexDataOffset));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(ld.IndexDataOffset));
                    }


                }
                #endregion


                // Mesh Data
                #region Mesh Groups Block

                var meshDataBlock = new List<byte>();

                var previousIndexCount = 0;
                short totalParts = 0;


                var previousIndexDataOffset = 0;
                var lod0VertexDataEntrySize0 = 0;
                var lod0VertexDataEntrySize1 = 0;
                var lod0VertexDataEntrySize2 = 0;
                byte lod0vertexStreamThing = 2;

                for (int mi = 0; mi < ttModel.MeshGroups.Count; mi++)
                {
                        // We only care about Lod [0].
                        var lod = ogMdl.LoDList[0];

                        bool addedMesh = mi >= lod.MeshCount;
                        var meshInfo = addedMesh ? null : lod.MeshDataList[mi].MeshInfo;

                        var vertexCount = addedMesh ? 0 : meshInfo.VertexCount;
                        var indexCount = addedMesh ? 0 : meshInfo.IndexCount;
                        var indexDataOffset = addedMesh ? 0 : meshInfo.IndexDataOffset;
                        var vertexDataOffset0 = addedMesh ? 0 : meshInfo.VertexDataOffset0;
                        var vertexDataOffset1 = addedMesh ? 0 : meshInfo.VertexDataOffset1;
                        var vertexDataOffset2 = addedMesh ? 0 : meshInfo.VertexDataOffset2;
                        byte vertexDataEntrySize0 = addedMesh ? (byte)lod0VertexDataEntrySize0 : (byte)perVertexDataSizes[mi][0];
                        byte vertexDataEntrySize1 = addedMesh ? (byte)lod0VertexDataEntrySize1 : (byte)perVertexDataSizes[mi][1];
                        byte vertexDataEntrySize2 = addedMesh ? (byte)lod0VertexDataEntrySize2 : (byte)perVertexDataSizes[mi][2];
                        short partCount = addedMesh ? (short)0 : meshInfo.MeshPartCount;
                        short materialIndex = addedMesh ? (short)0 : meshInfo.MaterialIndex;
                        short boneSetIndex = addedMesh ? (short)0 : (short)0;

                        // We rewrite the bottom bits where we have some idea what they're doing.
                        byte vertexStreamCountPlusFlags = addedMesh ? (byte)lod0vertexStreamThing : (byte) (meshInfo.VertexStreamCountUnknown & 0xF8);

                        var ttMeshGroup = ttModel.MeshGroups[mi];
                        vertexCount = (int)ttMeshGroup.VertexCount;
                        indexCount = (int)ttMeshGroup.IndexCount;
                        partCount = (short)ttMeshGroup.Parts.Count;
                        boneSetIndex = (short)mi;
                        materialIndex = ttModel.GetMaterialIndex(mi);


                        vertexStreamCountPlusFlags |= (byte)vertexStreamCounts[mi];

                        // This value seems to be [6 bitflags] - [2 bit stream count], with the lowest flag being something to do with the 8 byte weight format.
                        if (vertexInfoLists[mi].ContainsKey(VertexUsageType.BoneWeight) && vertexInfoLists[mi][VertexUsageType.BoneWeight][0] == VertexDataType.UByte8)
                        {
                            vertexStreamCountPlusFlags |= 4;
                        }
                        else
                        {
                            // Silly compiler warning about not liking ~4 as a byte
                            unchecked
                            {
                                vertexStreamCountPlusFlags &= ((byte) ~4);
                            }
                        }

                        // Add in any shape vertices.
                        if (ttModel.HasShapeData)
                        {
                            // These are effectively orphaned vertices until the shape
                            // data kicks in and rewrites the triangle index list.
                            foreach (var part in ttMeshGroup.Parts)
                            {
                                foreach (var shapePart in part.ShapeParts)
                                {
                                    if (shapePart.Key.StartsWith("shp_"))
                                    {
                                        vertexCount += shapePart.Value.Vertices.Count;
                                    }
                                }
                            }
                        }

                        if (lod0VertexDataEntrySize0 == 0)
                        {
                            // Used as a baseline value when adding new meshes.
                            lod0VertexDataEntrySize0 = vertexDataEntrySize0;
                            lod0VertexDataEntrySize1 = vertexDataEntrySize1;
                            lod0VertexDataEntrySize2 = vertexDataEntrySize2;
                            lod0vertexStreamThing = vertexStreamCountPlusFlags;
                        }


                        // Partless models strictly cannot have parts divisions.
                        if (ogMdl.Partless)
                        {
                            partCount = 0;
                        }

                        // Lots of offsets and counts.
                        // Pretty nuts & bolts with no frills.
                        meshDataBlock.AddRange(BitConverter.GetBytes(vertexCount));
                        meshDataBlock.AddRange(BitConverter.GetBytes(indexCount));
                        meshDataBlock.AddRange(BitConverter.GetBytes((short)materialIndex));

                        meshDataBlock.AddRange(BitConverter.GetBytes((short)(totalParts)));
                        meshDataBlock.AddRange(BitConverter.GetBytes((short)(partCount)));
                        totalParts += partCount;

                        meshDataBlock.AddRange(BitConverter.GetBytes((short)boneSetIndex));

                        // Can just use the real values from the already compiled geometry data.
                        meshDataBlock.AddRange(BitConverter.GetBytes(meshIndexOffsets[mi]));
                        meshDataBlock.AddRange(BitConverter.GetBytes(meshVertexOffsets[mi][0]));
                        meshDataBlock.AddRange(BitConverter.GetBytes(meshVertexOffsets[mi][1]));
                        meshDataBlock.AddRange(BitConverter.GetBytes(meshVertexOffsets[mi][2]));

                        meshDataBlock.Add(vertexDataEntrySize0);
                        meshDataBlock.Add(vertexDataEntrySize1);
                        meshDataBlock.Add(vertexDataEntrySize2);
                        meshDataBlock.Add(vertexStreamCountPlusFlags);

                        previousIndexDataOffset = indexDataOffset;
                        previousIndexCount = indexCount;
                    }




                #endregion

                #region Attribute Sets

                var attrPathOffsetList = attributeOffsetList;

                var attributePathDataBlock = new List<byte>();
                foreach (var attributeOffset in attrPathOffsetList)
                {
                    attributePathDataBlock.AddRange(BitConverter.GetBytes(attributeOffset));
                }

                #endregion

                // Unknown Data 1
                #region Unknown Data Block 1

                var unknownDataBlock1 = ogMdl.UnkData1.Unknown;


                #endregion

                // Mesh Part
                #region Mesh Part Data Block

                var meshPartDataBlock = new List<byte>();

                if (!ogMdl.Partless)
                {

                    short currentBoneOffset = 0;
                    var previousIndexOffset = 0;
                    previousIndexCount = 0;
                    var indexOffset = 0;

                    var lod = ogMdl.LoDList[0];
                    var partPadding = 0;

                    // Identify the correct # of meshes
                    var meshMax = ttModel.MeshGroups.Count;

                    for (int meshNum = 0; meshNum < meshMax; meshNum++)
                    {
                        // Test if we have both old and new data or not.
                        var ogGroup = lod.MeshDataList.Count > meshNum ? lod.MeshDataList[meshNum] : null;
                        var ttMeshGroup = ttModel.MeshGroups.Count > meshNum ? ttModel.MeshGroups[meshNum] : null;

                        // Identify correct # of parts.
                        var partMax = ttMeshGroup.Parts.Count;

                        // Totals for each group
                        var ogPartCount = ogGroup == null ? 0 : lod.MeshDataList[meshNum].MeshPartList.Count;
                        var newPartCount = ttMeshGroup == null ? 0 : ttMeshGroup.Parts.Count;


                        // Loop all the parts we should write.
                        for (var partNum = 0; partNum < partMax; partNum++)
                        {
                            // Get old and new data.
                            var ogPart = ogPartCount > partNum ? ogGroup.MeshPartList[partNum] : null;
                            var ttPart = newPartCount > partNum ? ttMeshGroup.Parts[partNum] : null;

                            var indexCount = 0;
                            short boneCount = 0;
                            uint attributeMask = 0;

                            // At LoD Zero we're not importing old FFXIV data, we're importing
                            // the new stuff.

                            // Recalculate Index Offset
                            if (meshNum == 0)
                            {
                                if (partNum == 0)
                                {
                                    indexOffset = 0;
                                }
                                else
                                {
                                    indexOffset = previousIndexOffset + previousIndexCount;
                                }
                            }
                            else
                            {
                                if (partNum == 0)
                                {
                                    indexOffset = previousIndexOffset + previousIndexCount + partPadding;
                                }
                                else
                                {
                                    indexOffset = previousIndexOffset + previousIndexCount;
                                }

                            }

                            attributeMask = ttModel.GetAttributeBitmask(meshNum, partNum);
                            indexCount = ttModel.MeshGroups[meshNum].Parts[partNum].TriangleIndices.Count;

                            // Count of bones for Mesh.
                            boneCount = (short)ttMeshGroup.Bones.Count;

                            // Calculate padding between meshes
                            if (partNum == newPartCount - 1)
                            {
                                var padd = (indexOffset + indexCount) % 8;

                                if (padd != 0)
                                {
                                    partPadding = 8 - padd;
                                }
                                else
                                {
                                    partPadding = 0;
                                }
                            }

                            meshPartDataBlock.AddRange(BitConverter.GetBytes(indexOffset));
                            meshPartDataBlock.AddRange(BitConverter.GetBytes(indexCount));
                            meshPartDataBlock.AddRange(BitConverter.GetBytes(attributeMask));
                            meshPartDataBlock.AddRange(BitConverter.GetBytes(currentBoneOffset));
                            meshPartDataBlock.AddRange(BitConverter.GetBytes(boneCount));

                            previousIndexCount = indexCount;
                            previousIndexOffset = indexOffset;
                            currentBoneOffset += boneCount;

                        }
                    }
                    
                }



                #endregion

                // Unknown Data 2
                #region Unknown Data Block 2
                var unknownDataBlock2 = ogMdl.UnkData2.Unknown;
                #endregion

                // Material Offset Data
                #region Material Data Block

                var matPathOffsetList = materialOffsetList;

                var matPathOffsetDataBlock = new List<byte>();
                foreach (var materialOffset in matPathOffsetList)
                {
                    matPathOffsetDataBlock.AddRange(BitConverter.GetBytes(materialOffset));
                }

                #endregion

                // Bone Strings Offset Data
                #region Bone Data Block

                var bonePathOffsetList = boneOffsetList;

                var bonePathOffsetDataBlock = new List<byte>();
                foreach (var boneOffset in bonePathOffsetList)
                {

                    bonePathOffsetDataBlock.AddRange(BitConverter.GetBytes(boneOffset));
                }

                #endregion

                // Bone Indices for meshes
                #region Mesh Bone Sets

                var boneSetsBlock = new List<byte>();

                var boneSetSize = 0;
                if (mdlVersion >= 6)
                {
                    List<List<byte>> meshBoneSets = new List<List<byte>>();
                    for (var mi = 0; mi < ttModel.MeshGroups.Count; mi++)
                    {
                        meshBoneSets.Add(ttModel.Getv6BoneSet(mi));
                    }

                    var offset = ttModel.MeshGroups.Count;
                    for (var mi = 0; mi < ttModel.MeshGroups.Count; mi++)
                    {
                        var dataSize = meshBoneSets[mi].Count;
                        short count = (short) (dataSize / 2);

                        boneSetsBlock.AddRange(BitConverter.GetBytes((short) 0));
                        boneSetsBlock.AddRange(BitConverter.GetBytes((short) (count)));

                    }

                    var boneSetStart = boneSetsBlock.Count;
                    for (var mi = 0; mi < ttModel.MeshGroups.Count; mi++)
                    {
                        var headerLocation = mi * 4;
                        var distance = (short)((boneSetsBlock.Count - headerLocation) / 4);

                        boneSetsBlock.AddRange(meshBoneSets[mi]);
                        if (meshBoneSets[mi].Count % 4 != 0)
                        {
                            boneSetsBlock.AddRange(new byte[2]);
                        }

                        // Copy in the offset information.
                        var offsetBytes = BitConverter.GetBytes(distance);
                        boneSetsBlock[headerLocation] = offsetBytes[0];
                        boneSetsBlock[headerLocation + 1] = offsetBytes[1];
                    }
                    var boneSetEnd = boneSetsBlock.Count;
                    boneSetSize = (boneSetEnd - boneSetStart) / 2;
                }
                else
                {
                    for (var mi = 0; mi < ttModel.MeshGroups.Count; mi++)
                    {
                        var originalBoneSet = ttModel.GetBoneSet(mi);
                        var data = originalBoneSet;
                        // Cut or pad to exactly 64 bones + blanks.  (v5 has a static array size of 128 bytes/64 shorts)
                        if (data.Count > 128)
                        {
                            data = data.GetRange(0, 128);
                        }
                        else if (data.Count < 128)
                        {
                            data.AddRange(new byte[128 - data.Count]);
                        }

                        // This is the array size... Which seems to need to be +1'd in Dawntrail for some reason.
                        if (mi == 0 || ttModel.MeshGroups[mi].Bones.Count >= 64)
                        {
                            data.AddRange(BitConverter.GetBytes(ttModel.MeshGroups[mi].Bones.Count));
                        } else
                        {
                            // DAWNTRAIL BENCHMARK HACKHACK - Add +1 to the bone count here to work around MDL v5 -> v6 in engine off-by-one error.
                            data.AddRange(BitConverter.GetBytes(ttModel.MeshGroups[mi].Bones.Count + 1));
                        }
                        
                        boneSetsBlock.AddRange(data);
                    }
                    var boneIndexListSize = boneSetsBlock.Count;
                }

                // Update the size listing.
                var sizeBytes = BitConverter.GetBytes((short)(boneSetSize));
                basicModelBlock[boneSetSizePointer] = sizeBytes[0];
                basicModelBlock[boneSetSizePointer + 1] = sizeBytes[1];

                // Higher LoD Bone sets are omitted.

                #endregion

                #region Shape Stuff

                var FullShapeDataBlock = new List<byte>();
                if (ttModel.HasShapeData)
                {
                    #region Shape Part Counts

                    var meshShapeInfoDataBlock = new List<byte>();

                    var shapeInfoCount = ogMdl.MeshShapeData.ShapeInfoList.Count;
                    var shapePartCounts = ttModel.ShapePartCounts;

                    short runningSum = 0;
                    for (var sIdx = 0; sIdx < ttModel.ShapeNames.Count; sIdx++)
                    {

                        meshShapeInfoDataBlock.AddRange(BitConverter.GetBytes(shapeOffsetList[sIdx]));
                        var count = shapePartCounts[sIdx];

                        for (var l = 0; l < ogMdl.LoDList.Count; l++)
                        {
                            if (l == 0)
                            {
                                // LOD 0
                                meshShapeInfoDataBlock.AddRange(BitConverter.GetBytes((short)runningSum));
                                runningSum += count;

                            }
                            else
                            {
                                // LOD 1+
                                meshShapeInfoDataBlock.AddRange(BitConverter.GetBytes((short)0));
                            }
                        }
                        for (var l = 0; l < ogMdl.LoDList.Count; l++)
                        {
                            if (l == 0)
                            {
                                // LOD 0
                                meshShapeInfoDataBlock.AddRange(BitConverter.GetBytes((short)count));

                            }
                            else
                            {
                                // LOD 1+
                                meshShapeInfoDataBlock.AddRange(BitConverter.GetBytes((short)0));
                            }
                        }
                    }

                    FullShapeDataBlock.AddRange(meshShapeInfoDataBlock);

                    #endregion

                    // Mesh Shape Index Info
                    #region Shape Parts Data Block

                    var shapePartsDataBlock = new List<byte>();
                    int offset = 0;

                    foreach (var shapePart in rawShapeData.ShapeList)
                    {
                        shapePartsDataBlock.AddRange(BitConverter.GetBytes(meshIndexOffsets[shapePart.MeshId]));
                        shapePartsDataBlock.AddRange(BitConverter.GetBytes(shapePart.IndexReplacements.Count));
                        shapePartsDataBlock.AddRange(BitConverter.GetBytes(offset));

                        offset += shapePart.IndexReplacements.Count;
                    }

                    FullShapeDataBlock.AddRange(shapePartsDataBlock);

                    #endregion

                    // Mesh Shape Data
                    #region Raw Shape Data Data Block

                    var meshShapeDataBlock = new List<byte>();

                    foreach (var p in rawShapeData.ShapeList)
                    {
                        foreach (var r in p.IndexReplacements)
                        {
                            if (r.Value > ushort.MaxValue)
                            {
                                throw new InvalidDataException("Mesh Group " + p.MeshId + " has too many total vertices/triangle indices.\nRemove some vertices/faces/shapes or split them across multiple mesh groups.");
                            }
                            meshShapeDataBlock.AddRange(BitConverter.GetBytes((ushort)r.Key));
                            meshShapeDataBlock.AddRange(BitConverter.GetBytes((ushort)r.Value));
                        }
                    }

                    FullShapeDataBlock.AddRange(meshShapeDataBlock);

                    #endregion
                }

                #endregion

                // Bone Index Part
                #region Part Bone Sets

                // These are referential arrays to subsets of their parent mesh bone set.
                // Their length is determined by the Part header's BoneCount field.
                var partBoneSetsBlock = new List<byte>();
                if (!ogMdl.Partless)
                {
                    {
                        var bones = ttModel.Bones;

                        for (var j = 0; j < ttModel.MeshGroups.Count; j++)
                        {
                            for (short i = 0; i < ttModel.MeshGroups[j].Bones.Count; i++)
                            {
                                // It's probably not perfectly performant in game, but we can just
                                // write every bone from the parent set back in here.
                                partBoneSetsBlock.AddRange(BitConverter.GetBytes(i));
                            }
                        }

                        // Higher LoDs omitted (they're given 0 bones)

                    }
                }

                partBoneSetsBlock.InsertRange(0, BitConverter.GetBytes((int)(partBoneSetsBlock.Count)));


                #endregion

                // Padding 
                #region Padding Data Block

                var paddingDataBlock = new List<byte>();

                paddingDataBlock.Add(ogMdl.PaddingSize);
                paddingDataBlock.AddRange(ogMdl.PaddedBytes);

                #endregion

                // Bounding Box
                #region Bounding Box Data Block

                var boundingBoxDataBlock = new List<byte>();

                // There are 4 bounding boxes in sequence, defined by a min and max point.
                // The 4 boxes are:
                    // "BoundingBox"                - Bounding box from Origin, encompasing model.
                    // "ModelBoundingBox"           - Bounding box only around the model itself.
                    // "WaterBoundingbox"           - Typically full 0s
                    // "VerticalFogBoundingBox"     - Typically full 0s
                for (int i = 0; i < 4; i++)
                {
                    if (i < 2)
                    {
                        // First bounding box is bounds from the origin.
                        boundingBoxDataBlock.AddRange(BitConverter.GetBytes(i == 0 && minVect.X > 0 ? 0.0f : minVect.X));
                        boundingBoxDataBlock.AddRange(BitConverter.GetBytes(i == 0 && minVect.Y > 0 ? 0.0f : minVect.Y));
                        boundingBoxDataBlock.AddRange(BitConverter.GetBytes(i == 0 && minVect.Z > 0 ? 0.0f : minVect.Z));
                        boundingBoxDataBlock.AddRange(BitConverter.GetBytes(1.0f));

                        boundingBoxDataBlock.AddRange(BitConverter.GetBytes(i == 0 && maxVect.X < 0 ? 0.0f : maxVect.X));
                        boundingBoxDataBlock.AddRange(BitConverter.GetBytes(i == 0 && maxVect.Y < 0 ? 0.0f : maxVect.Y));
                        boundingBoxDataBlock.AddRange(BitConverter.GetBytes(i == 0 && maxVect.Z < 0 ? 0.0f : maxVect.Z));
                        boundingBoxDataBlock.AddRange(BitConverter.GetBytes(1.0f));
                    } else
                    {
                        // Water and Vertical Fog bounding boxes are always 0 currently.
                        // Could use data from the original model, but better to find some models with the values first.
                        boundingBoxDataBlock.AddRange(new byte[32]);
                    }
                }

                // Afterwards there are is a bounding box for every bone.
                // Again, we'll be lazy and just use the full model bounding box for each.
                // Unsure what these are actually used for since there seems no difference
                // in model function whether these have real data or no data.
                var boneBoundingBoxDataBlock = new List<byte>();
                for (var i = 0; i < ttModel.Bones.Count; i++)
                {
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(minVect.X));
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(minVect.Y));
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(minVect.Z));
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(1.0f));

                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(maxVect.X));
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(maxVect.Y));
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(maxVect.Z));
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(1.0f));
                }

                // ogMdl.ModelData.UnknownBoundingBoxCount
                // If ^ is greater than 0, we're supposed to write some kind of extra bounding boxes here.
                // Seems to only exist for models with no bones, like furniture?



                #endregion


                #region LoD Block
                // LoD block is the most complex block, and so we write it last, even though it's actually pretty early on in the file.

                // Combined Data Block Sizes
                // This is the offset to the beginning of the vertex data
                var combinedDataBlockSize = _MdlHeaderSize + vertexInfoBlock.Count + pathInfoBlock.Count + basicModelBlock.Count + unknownDataBlock0.Length + (60 * ogMdl.LoDList.Count) + extraLodDataBlock.Count + meshDataBlock.Count +
                    attributePathDataBlock.Count + (unknownDataBlock1?.Length ?? 0) + meshPartDataBlock.Count + unknownDataBlock2.Length + matPathOffsetDataBlock.Count + bonePathOffsetDataBlock.Count +
                    boneSetsBlock.Count + FullShapeDataBlock.Count + partBoneSetsBlock.Count + paddingDataBlock.Count + boundingBoxDataBlock.Count + boneBoundingBoxDataBlock.Count;

                var lodDataBlock = new List<byte>();
                List<int> indexStartInjectPointers = new List<int>();
                for(int l = 0; l < ogMdl.LoDList.Count; l++)
                {
                    var originalLod = ogMdl.LoDList[l];

                    int vertexDataOffset = 0;
                    int vertexDataSize = 0;
                    int indexDataOffset = 0;
                    int indexDataSize = 0;

                    // LoD 0 values.
                    short meshOffset = 0;
                    short totalMeshes = (short)geometryData.Count;
                    vertexDataOffset = combinedDataBlockSize;
                    vertexDataSize = vertexDataBlock.Count;
                    indexDataOffset = vertexDataOffset + vertexDataSize;
                    indexDataSize = indexDataBlock.Count;

                    // LoD 1+ is always empty in our imports, so the real values for their offsets are just the end of LoD 0's data with 0s for counts
                    if (l > 0)
                    {
                        meshOffset = totalMeshes;
                        totalMeshes = 0;
                        vertexDataOffset = indexDataOffset + indexDataSize;
                        indexDataOffset = indexDataOffset + indexDataSize;
                        indexDataSize = 0;
                        vertexDataSize = 0;
                    }

                    // We add any additional meshes to the offset if we added any through advanced importing, otherwise additionalMeshCount stays at 0
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)meshOffset));
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)totalMeshes));

                    // Distances to kick in LoD effects
                    lodDataBlock.AddRange(BitConverter.GetBytes(_ReallyLongDistance * (l + 1))); // Model LoD
                    lodDataBlock.AddRange(BitConverter.GetBytes(_KindOfLongDistance * (l + 1))); // Texture LoD - We're actually okay with this one since we have MipMaps.

                    // Water Mesh Index and Count.
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)(totalMeshes)));
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)0));

                    // Shadow Mesh Index and Count
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)(totalMeshes)));
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)0));

                    // Terrain Shadow Mesh Index and Count
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)totalMeshes));
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)0));

                    // Fog Mesh Index and Count
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)totalMeshes));
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)0));

                    // Edge Geometry Size and Offset
                    lodDataBlock.AddRange(BitConverter.GetBytes((int)0));
                    lodDataBlock.AddRange(BitConverter.GetBytes((int)indexDataOffset));

                    // Unknown x2 - Probably Another Size and Offset?
                    lodDataBlock.AddRange(BitConverter.GetBytes(originalLod.Unknown6));
                    lodDataBlock.AddRange(BitConverter.GetBytes(originalLod.Unknown7));

                    // Vertex & Index Sizes
                    lodDataBlock.AddRange(BitConverter.GetBytes(vertexDataSize));
                    lodDataBlock.AddRange(BitConverter.GetBytes(indexDataSize));

                    // Vertex & Index Offsets
                    lodDataBlock.AddRange(BitConverter.GetBytes(vertexDataOffset));
                    lodDataBlock.AddRange(BitConverter.GetBytes(indexDataOffset));
                }
                #endregion


                #region Model Data Segment Compliation

                // Combine all the data blocks included in the final compressed model data block.
                // This is basically everything except the header, vertex info, and geometry data.
                List<byte> modelDataBlock = new List<byte>();
                modelDataBlock.AddRange(pathInfoBlock);
                modelDataBlock.AddRange(basicModelBlock);
                modelDataBlock.AddRange(unknownDataBlock0);
                modelDataBlock.AddRange(lodDataBlock);
                modelDataBlock.AddRange(extraLodDataBlock);
                modelDataBlock.AddRange(meshDataBlock);
                modelDataBlock.AddRange(attributePathDataBlock);
                if (unknownDataBlock1 != null)
                {
                    modelDataBlock.AddRange(unknownDataBlock1);
                }
                modelDataBlock.AddRange(meshPartDataBlock);
                modelDataBlock.AddRange(unknownDataBlock2);
                modelDataBlock.AddRange(matPathOffsetDataBlock);
                modelDataBlock.AddRange(bonePathOffsetDataBlock);
                modelDataBlock.AddRange(boneSetsBlock);
                modelDataBlock.AddRange(FullShapeDataBlock);
                modelDataBlock.AddRange(partBoneSetsBlock);
                modelDataBlock.AddRange(paddingDataBlock);
                modelDataBlock.AddRange(boundingBoxDataBlock);
                modelDataBlock.AddRange(boneBoundingBoxDataBlock);

                if(combinedDataBlockSize != modelDataBlock.Count + _MdlHeaderSize + vertexInfoBlock.Count)
                {
                    throw new Exception("Model Data Block offset calculations invalid.  Resulting MDL file would be corrupt.");
                }
                #endregion


                // Create the MDL Header and compile the final data.
                #region MDL File Header Creation
                var header = new List<byte>();

                // Signature
                header.AddRange(BitConverter.GetBytes((short)mdlVersion));
                header.AddRange(BitConverter.GetBytes((short)256));

                // Model Data Stuff Sizes
                header.AddRange(BitConverter.GetBytes(vertexInfoBlock.Count));
                header.AddRange(BitConverter.GetBytes(modelDataBlock.Count));

                // Counts of stuff that goes in the Type3 file header.
                header.AddRange(BitConverter.GetBytes((ushort)meshCount));
                header.AddRange(BitConverter.GetBytes((ushort)ttModel.Materials.Count));

                var vBuffer0Offset = _MdlHeaderSize +  vertexInfoBlock.Count + modelDataBlock.Count;
                var iBuffer0Offset = vBuffer0Offset + vertexDataBlock.Count;
                var vBuffer1Offset = iBuffer0Offset + indexDataBlock.Count;

                // Vertex Buffer Offsets
                header.AddRange(BitConverter.GetBytes(vBuffer0Offset));
                header.AddRange(BitConverter.GetBytes(vBuffer1Offset));
                header.AddRange(BitConverter.GetBytes(vBuffer1Offset));

                // Index Buffer Offsets
                header.AddRange(BitConverter.GetBytes(iBuffer0Offset));
                header.AddRange(BitConverter.GetBytes(vBuffer1Offset));
                header.AddRange(BitConverter.GetBytes(vBuffer1Offset));

                // Vertex Buffer Sizes
                header.AddRange(BitConverter.GetBytes(vertexDataBlock.Count));
                header.AddRange(BitConverter.GetBytes(0));
                header.AddRange(BitConverter.GetBytes(0));

                // Index Buffer Sizes
                header.AddRange(BitConverter.GetBytes(indexDataBlock.Count));
                header.AddRange(BitConverter.GetBytes(0));
                header.AddRange(BitConverter.GetBytes(0));

                // LoD
                header.Add(_LoDCount);

                // Flags - We typically just write flag 0x01 on (Index streaming).
                header.Add((byte) 1);

                // Standard padding bytes.
                header.AddRange(new byte[2]);

                // Final Uncompressed MDL File Compilation.
                var mdlFile = header.Concat(vertexInfoBlock).Concat(modelDataBlock).Concat(vertexDataBlock).Concat(indexDataBlock).ToArray();
                #endregion

                return mdlFile;
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        /// <summary>
        /// Compresses a MDL file into a valid Type3 datafile.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public async Task<byte[]> CompressMdlFile(byte[] data)
        {

            #region MDL Header Reading
            // Read sizes and offsets.
            const int _VertexInfoSizeOffset = 0x04;
            const int _ModelDataSizeOffset = 0x08;
            const ushort _MeshCountOffset = 12;
            const ushort _MaterialCountOffset = 14;
            const int _LodMax = 3;

            var signature = BitConverter.ToUInt32(data, 0);

            var vertexInfoUncompOffset = _MdlHeaderSize;
            var vertexInfoUncompSize = BitConverter.ToInt32(data, _VertexInfoSizeOffset);

            var modelUncompDataOffset = vertexInfoUncompOffset + vertexInfoUncompSize;
            var modelUncompDataSize = BitConverter.ToInt32(data, _ModelDataSizeOffset);

            var meshCount = BitConverter.ToUInt16(data, _MeshCountOffset);
            var materialCount = BitConverter.ToUInt16(data, _MaterialCountOffset);

            var offset = 16;

            uint[] vertexDataOffsets = Dat.Read3IntBuffer(data, offset);
            offset += (sizeof(uint) * _LodMax);
            uint[] indexDataOffsets = Dat.Read3IntBuffer(data, offset);
            offset += (sizeof(uint) * _LodMax);
            uint[] vertexDataSizes = Dat.Read3IntBuffer(data, offset);
            offset += (sizeof(uint) * _LodMax);
            uint[] indexDataSizes = Dat.Read3IntBuffer(data, offset);
            offset += (sizeof(uint) * _LodMax);

            var lodCount = data[offset];
            var flags = data[offset + 1];
            #endregion

            #region Data Compression
            // We can now compress the data blocks.
            var compressedMDLData = new List<byte>();

            // Vertex Info Compression
            var vertexInfoBlock = data.Skip(vertexInfoUncompOffset).Take(vertexInfoUncompSize).ToList();
            var compressedVertexInfoParts = await Dat.CompressData(vertexInfoBlock);
            foreach (var block in compressedVertexInfoParts)
            {
                compressedMDLData.AddRange(block);
            }

            // Model Data Compression
            var modelDataBlock = data.Skip(modelUncompDataOffset).Take(modelUncompDataSize).ToList();
            List<byte[]> compressedModelDataParts = new List<byte[]>();
            compressedModelDataParts = await Dat.CompressData(modelDataBlock);
            foreach (var block in compressedModelDataParts)
            {
                compressedMDLData.AddRange(block);
            }

            // Vertex & Index Data Compression
            var compressedVertexDataParts = new List<List<byte[]>>();
            var compressedIndexDataParts = new List<List<byte[]>>();
            for (int i = 0; i < _LodMax; i++)
            {
                // Written as [Vertex Block LoD0] [Index Block Lod0] [Vertex LoD1] ....
                var uncompressedVertex = data.Skip((int) vertexDataOffsets[i]).Take((int)vertexDataSizes[i]).ToList();
                var compressedVertex = await Dat.CompressData(uncompressedVertex);
                var uncompressedIndex = data.Skip((int)indexDataOffsets[i]).Take((int)indexDataSizes[i]).ToList();
                var compressedIndex = await Dat.CompressData(uncompressedIndex);

                foreach (var block in compressedVertex)
                {
                    compressedMDLData.AddRange(block);
                }
                foreach (var block in compressedIndex)
                {
                    compressedMDLData.AddRange(block);
                }

                compressedVertexDataParts.Add(compressedVertex);
                compressedIndexDataParts.Add(compressedIndex);
            }

            #endregion

            #region Type 3 Header Writing

            var datHeader = new List<byte>();

            // This is the most common size of header for models
            var headerLength = 256;

            // 1 Block for vertex info + model data + geometry data.
            var blockCount = compressedVertexInfoParts.Count + compressedModelDataParts.Count + compressedVertexDataParts.Count + compressedIndexDataParts.Count;

            // If the data is large enough, the header length goes to the next larger size (add 128 bytes)
            if (blockCount > 24)
            {
                var remainingBlocks = blockCount - 24;
                var bytesUsed = remainingBlocks * 2;
                var extensionNeeeded = (bytesUsed / 128) + 1;
                var newSize = 256 + (extensionNeeeded * 128);
                headerLength = newSize;
            }

            // Header Length
            datHeader.AddRange(BitConverter.GetBytes(headerLength));

            // Data Type (models are type 3 data)
            datHeader.AddRange(BitConverter.GetBytes(3));

            // Model files are comprised of a fixed length header +  The Vertex Headers/Infos Structures + the larger general model metadata block + the geometry data blocks.
            var uncompressedSize = _MdlHeaderSize + vertexInfoBlock.Count + modelDataBlock.Count + vertexDataSizes.Sum(x => x) + indexDataSizes.Sum(x => x);
            datHeader.AddRange(BitConverter.GetBytes((uint) uncompressedSize));

            // Max Buffer Size?
            datHeader.AddRange(BitConverter.GetBytes(compressedMDLData.Count / 128 + 16));
            // Buffer Size
            datHeader.AddRange(BitConverter.GetBytes(compressedMDLData.Count / 128));

            // Mdl Version / Signature
            datHeader.AddRange(BitConverter.GetBytes(signature));


            // Vertex Info Block Uncompressed Size
            datHeader.AddRange(BitConverter.GetBytes(Dat.Pad(vertexInfoBlock.Count, 128)));
            // Model Data Block Uncompressed Size
            datHeader.AddRange(BitConverter.GetBytes(Dat.Pad(modelDataBlock.Count, 128)));
            // Vertex Data Block Uncompressed Sizes
            datHeader.AddRange(BitConverter.GetBytes(Dat.Pad((int) vertexDataSizes[0], 128)));
            datHeader.AddRange(BitConverter.GetBytes(Dat.Pad((int) vertexDataSizes[1], 128)));
            datHeader.AddRange(BitConverter.GetBytes(Dat.Pad((int) vertexDataSizes[2], 128)));
            // Edge Geometry Uncompressed Sizes
            datHeader.AddRange(BitConverter.GetBytes(0));
            datHeader.AddRange(BitConverter.GetBytes(0));
            datHeader.AddRange(BitConverter.GetBytes(0));
            // Index Data Uncompressed Sizes
            datHeader.AddRange(BitConverter.GetBytes(Dat.Pad((int)indexDataSizes[0], 128)));
            datHeader.AddRange(BitConverter.GetBytes(Dat.Pad((int)indexDataSizes[1], 128)));
            datHeader.AddRange(BitConverter.GetBytes(Dat.Pad((int)indexDataSizes[2], 128)));

            // Vertex Info Total Compressed Size
            datHeader.AddRange(BitConverter.GetBytes(compressedVertexInfoParts.Sum(x => x.Length)));
            // Model Data Total Compressed Size
            datHeader.AddRange(BitConverter.GetBytes(compressedModelDataParts.Sum(x => x.Length)));
            // Vertex Data Total Compressed Sizes
            datHeader.AddRange(BitConverter.GetBytes(compressedVertexDataParts[0].Sum(x => x.Length)));
            datHeader.AddRange(BitConverter.GetBytes(compressedVertexDataParts[1].Sum(x => x.Length)));
            datHeader.AddRange(BitConverter.GetBytes(compressedVertexDataParts[2].Sum(x => x.Length)));
            // Edge Geometry Total Compressed Sizes
            datHeader.AddRange(BitConverter.GetBytes(0));
            datHeader.AddRange(BitConverter.GetBytes(0));
            datHeader.AddRange(BitConverter.GetBytes(0));
            // Index Data Total Compressed Sizes
            datHeader.AddRange(BitConverter.GetBytes(compressedIndexDataParts[0].Sum(x => x.Length)));
            datHeader.AddRange(BitConverter.GetBytes(compressedIndexDataParts[1].Sum(x => x.Length)));
            datHeader.AddRange(BitConverter.GetBytes(compressedIndexDataParts[2].Sum(x => x.Length)));


            // Compressed Offsets
            var vertexInfoOffset = 0;
            var modelDataOffset = vertexInfoOffset + compressedVertexInfoParts.Sum(x => x.Length);

            var vertexDataBlock0Offset = modelDataOffset + compressedModelDataParts.Sum(x => x.Length);
            var indexDataBlock0Offset = vertexDataBlock0Offset + compressedVertexDataParts[0].Sum(x => x.Length);

            var vertexDataBlock1Offset = indexDataBlock0Offset + compressedIndexDataParts[0].Sum(x => x.Length);
            var indexDataBlock1Offset = vertexDataBlock1Offset + compressedVertexDataParts[1].Sum(x => x.Length);

            var vertexDataBlock2Offset = indexDataBlock1Offset + compressedIndexDataParts[1].Sum(x => x.Length);
            var indexDataBlock2Offset = vertexDataBlock2Offset + compressedVertexDataParts[2].Sum(x => x.Length);
            
            // Vertex Info Compressed Offset
            datHeader.AddRange(BitConverter.GetBytes(vertexInfoOffset));
            // Model Data Compressed Offset
            datHeader.AddRange(BitConverter.GetBytes(modelDataOffset));
            // Vertex Data Compressed Offsets
            datHeader.AddRange(BitConverter.GetBytes(vertexDataBlock0Offset));
            datHeader.AddRange(BitConverter.GetBytes(vertexDataBlock1Offset));
            datHeader.AddRange(BitConverter.GetBytes(vertexDataBlock2Offset));
            // Edge Geometry Compressed Offsets
            datHeader.AddRange(BitConverter.GetBytes(0));
            datHeader.AddRange(BitConverter.GetBytes(0));
            datHeader.AddRange(BitConverter.GetBytes(0));
            // Index Data Compressed Offsets
            datHeader.AddRange(BitConverter.GetBytes(indexDataBlock0Offset));
            datHeader.AddRange(BitConverter.GetBytes(indexDataBlock1Offset));
            datHeader.AddRange(BitConverter.GetBytes(indexDataBlock2Offset));

            // Data Block Indexes
            var vertexInfoDataBlockIndex = 0;
            var modelDataBlockIndex = vertexInfoDataBlockIndex + compressedVertexInfoParts.Count;

            var vertexDataBlock0 = modelDataBlockIndex + compressedModelDataParts.Count;
            var indexDataBlock0 = vertexDataBlock0 + compressedVertexDataParts[0].Count;

            var vertexDataBlock1 = indexDataBlock0 + compressedIndexDataParts[0].Count;
            var indexDataBlock1 = vertexDataBlock1 + compressedVertexDataParts[1].Count;

            var vertexDataBlock2 = indexDataBlock1 + compressedIndexDataParts[1].Count;
            var indexDataBlock2 = vertexDataBlock2 + compressedVertexDataParts[2].Count;

            // Vertex Info Block Index
            datHeader.AddRange(BitConverter.GetBytes((short)vertexInfoDataBlockIndex));
            // Model Data Block Index
            datHeader.AddRange(BitConverter.GetBytes((short)modelDataBlockIndex));
            // Vertex Data Block LoD[0] Indexes
            datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataBlock0));
            datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataBlock1));
            datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataBlock2));
            // Edge Geometry Data Block Indexes
            datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock0));
            datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock1));
            datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock2));
            // Index Data Block Indexes
            datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock0));
            datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock1));
            datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock2));


            // Data Block Counts
            // Vertex Info Part Count
            datHeader.AddRange(BitConverter.GetBytes((short)compressedVertexInfoParts.Count));
            // Model Data Part Count
            datHeader.AddRange(BitConverter.GetBytes((ushort)compressedModelDataParts.Count));
            // Vertex Data Block LoD[0] part count
            datHeader.AddRange(BitConverter.GetBytes((ushort)compressedVertexDataParts[0].Count));
            datHeader.AddRange(BitConverter.GetBytes((ushort)compressedVertexDataParts[1].Count));
            datHeader.AddRange(BitConverter.GetBytes((ushort)compressedVertexDataParts[2].Count));
            // Edge Geometry Counts
            datHeader.AddRange(BitConverter.GetBytes((short)0));
            datHeader.AddRange(BitConverter.GetBytes((short)0));
            datHeader.AddRange(BitConverter.GetBytes((short)0));

            // Index Data Block Counts
            datHeader.AddRange(BitConverter.GetBytes((ushort)compressedIndexDataParts[0].Count));
            datHeader.AddRange(BitConverter.GetBytes((ushort)compressedIndexDataParts[1].Count));
            datHeader.AddRange(BitConverter.GetBytes((ushort)compressedIndexDataParts[2].Count));


            // Mesh Count
            datHeader.AddRange(BitConverter.GetBytes((ushort)meshCount));
            // Material Count
            datHeader.AddRange(BitConverter.GetBytes((ushort)materialCount));
            // LoD Count
            datHeader.Add(lodCount);
            // Flags, specifically flag 1 enables index streaming.
            datHeader.Add(flags);
            // Padding
            datHeader.AddRange(BitConverter.GetBytes((short)0));


            // ==== Compressed Block Sizes in order go here ==== //

            // Vertex Info Compressed Block Sizes
            for (var i = 0; i < compressedVertexInfoParts.Count; i++)
            {
                datHeader.AddRange(BitConverter.GetBytes((ushort)compressedVertexInfoParts[i].Length));
            }

            // Model Data Padded Size
            for (var i = 0; i < compressedModelDataParts.Count; i++)
            {
                datHeader.AddRange(BitConverter.GetBytes((ushort)compressedModelDataParts[i].Length));
            }

            // Vertex/Index Info Compressed Block Sizes
            for (var l = 0; l < _LodMax; l++)
            {
                for(int i = 0; i < compressedVertexDataParts[l].Count; i++)
                {
                    datHeader.AddRange(BitConverter.GetBytes((ushort)compressedVertexDataParts[l][i].Length));
                }

                // If we had edge geometry blocks, they would go here.

                for (int i = 0; i < compressedIndexDataParts[l].Count; i++)
                {
                    datHeader.AddRange(BitConverter.GetBytes((ushort)compressedIndexDataParts[l][i].Length));
                }
            }


            // Pad out remaining header space.
            if (datHeader.Count != headerLength)
            {
                var headerEnd = headerLength - datHeader.Count % headerLength;
                datHeader.AddRange(new byte[headerEnd]);
            }

            // Prepend the header to the Compressed MDL data to create the final type3 File.
            compressedMDLData.InsertRange(0, datHeader);
            #endregion

            return compressedMDLData.ToArray();
            
        }


        /// <summary>
        /// Converts the TTTModel Geometry into the raw byte blocks FFXIV expects.
        /// </summary>
        /// <param name="colladaMeshDataList">The list of mesh data obtained from the imported collada file</param>
        /// <param name="itemType">The item type</param>
        /// <returns>A dictionary containing the vertex byte data per mesh</returns>
        private Dictionary<int, VertexByteData> GetBasicGeometryData(TTModel ttModel, List<Dictionary<VertexUsageType, List<VertexDataType>>> vertexInfoDict, Action<bool, string> loggingFunction)
        {
            var importDataDictionary = new Dictionary<int, VertexByteData>();

            var meshNumber = 0;


            // Add the first vertex data set to the ImportData list
            // This contains [ Position, Bone Weights, Bone Indices]
            for(var mi = 0; mi < ttModel.MeshGroups.Count; mi++)
            {
                var m = ttModel.MeshGroups[mi];
                var importData = new VertexByteData()
                {
                    VertexData0 = new List<byte>(),
                    VertexData1 = new List<byte>(),
                    IndexData = new List<byte>(),
                    VertexCount = (int)m.VertexCount,
                    IndexCount = (int)m.IndexCount
                };

                foreach (var p in m.Parts)
                {
                    foreach (var v in p.Vertices)
                    {
                        WriteVertex(importData, vertexInfoDict[mi], ttModel, v, loggingFunction);
                    }

                }

                var count = m.IndexCount;
                for (var i = 0; i < count; i++)
                {
                    var index = m.GetIndexAt(i);
                    importData.IndexData.AddRange(BitConverter.GetBytes(((ushort)index)));
                }
                // Add the import data to the dictionary
                importDataDictionary.Add(meshNumber, importData);
                meshNumber++;
            }

            return importDataDictionary;
        }

        /// <summary>
        /// Converts a given Vector 3 Binormal into the the byte4 format SE uses for storing Binormal data.
        /// </summary>
        /// <param name="normal"></param>
        /// <param name="handedness"></param>
        /// <returns></returns>
        private static List<byte> ConvertVectorBinormalToBytes(Vector3 normal, int handedness)
        {
            // These four byte vector values are represented as
            // [ Byte x, Byte y, Byte z, Byte handedness(0/255) ]


            // Now, this is where things get a little weird compared to storing most 3D Models.
            // SE's standard format is to include BINOMRAL(aka Bitangent) data, but leave TANGENT data out, to be calculated on the fly from the BINORMAL data.
            // This is kind of reverse compared to most math you'll find where the TANGENT is kept, and the BINORMAL is calculated on the fly. (Or both are kept/both are generated on load)

            // The Binormal data has already had the handedness applied to generate an appropriate binormal, but we store
            // that handedness after for use when the game (or textools) regenerates the Tangent from the Normal + Binormal.

            var bytes = new List<byte>(4);
            var vec = normal;
            vec.Normalize();


            // The possible range of -1 to 1 Vector X/Y/Z Values are compressed
            // into a 0-255 range.

            // A simple way to solve this cleanly is to translate the vector by [1] in all directions
            // So the vector's range is 0 to 2.
            vec += Vector3.One;

            // And then multiply the resulting value times (255 / 2), and round off the result.
            // This helps minimize errors that arise from quirks in floating point arithmetic.
            var x = (byte)Math.Round(vec.X * (255f / 2f));
            var y = (byte)Math.Round(vec.Y * (255f / 2f));
            var z = (byte)Math.Round(vec.Z * (255f / 2f));


            bytes.Add(x);
            bytes.Add(y);
            bytes.Add(z);

            // Add handedness bit
            if (handedness > 0)
            {
                bytes.Add(0);
            }
            else
            {
                bytes.Add(255);
            }

            return bytes;
        }


        private bool WriteVectorData(List<byte> buffer, Dictionary<VertexUsageType, List<VertexDataType>> vertexInfoList, VertexUsageType usage, TTVertex v)
        {
            if(!vertexInfoList.ContainsKey(usage) || vertexInfoList[usage].Count <= 0)
            {
                return false;
            }
            if(vertexInfoList[usage].Count > 1)
            {
                throw new Exception("System does not know how to handle writing a model with multiple " + usage.ToString() + " data streams.");
            }

            Vector3 value;
            bool handedness = true;
            var dataType = vertexInfoList[usage][0];
            var wDefault = 0;
            switch (usage)
            {
                case VertexUsageType.Normal:
                    value = v.Normal;
                    break;
                case VertexUsageType.Position:
                    value = v.Position;
                    wDefault = 1;
                    break;
                case VertexUsageType.Binormal:
                    value = v.Binormal;
                    handedness = v.Handedness;
                    break;
                case VertexUsageType.Tangent:
                    value = v.Tangent;
                    handedness = !v.Handedness;
                    break;
                default:
                    return false;
            }

            return WriteVectorData(buffer, dataType, value, handedness, wDefault);
        }

        private bool WriteVectorData(List<byte> buffer, VertexDataType dataType, Vector3 data, bool handedness = true, int wDefault = 0)
        {
            Half hx, hy, hz;

            if (dataType == VertexDataType.Half4)
            {
                hx = new Half(data[0]);
                hy = new Half(data[1]);
                hz = new Half(data[2]);

                buffer.AddRange(BitConverter.GetBytes(hx.RawValue));
                buffer.AddRange(BitConverter.GetBytes(hy.RawValue));
                buffer.AddRange(BitConverter.GetBytes(hz.RawValue));

                // Half float positions have a W coordinate that is typically defaulted to either 0 (position data) or 1 (normal data).
                var w = new Half(wDefault);
                buffer.AddRange(BitConverter.GetBytes(w.RawValue));

            }
            // Everything else has positions as singles 
            else if (dataType == VertexDataType.Float3)
            {
                buffer.AddRange(BitConverter.GetBytes(data[0]));
                buffer.AddRange(BitConverter.GetBytes(data[1]));
                buffer.AddRange(BitConverter.GetBytes(data[2]));
            }
            else if (dataType == VertexDataType.Ubyte4n || dataType == VertexDataType.Ubyte4n)
            {
                int handednessInt = handedness ? -1 : 1;
                buffer.AddRange(ConvertVectorBinormalToBytes(data, handednessInt));

            }
            return true;
        }

        /// <summary>
        /// Writes vertex data to the given import structures, and returns the total byte size of the index written.
        /// </summary>
        /// <param name="importData"></param>
        /// <param name="vertexInfoList"></param>
        /// <param name="model"></param>
        /// <param name="v"></param>
        /// <returns></returns>
        private int WriteVertex(VertexByteData importData, Dictionary<VertexUsageType, List<VertexDataType>> vertexInfoList, TTModel model, TTVertex v, Action<bool, string> loggingFunction)
        {
            // Positions for Weapon and Monster item types are half precision floating points
            var start0 = importData.VertexData0.Count;
            var start1 = importData.VertexData1.Count;

            WriteVectorData(importData.VertexData0, vertexInfoList, VertexUsageType.Position, v);

            // Furniture items do not have bone data
            if (model.HasWeights)
            {
                if (vertexInfoList[VertexUsageType.BoneIndex][0] == VertexDataType.UByte8)
                {
                    ModelModifiers.CleanWeight(v, 8, loggingFunction);

                    // 8 Byte stye...
                    importData.VertexData0.Add(v.Weights[0]);
                    importData.VertexData0.Add(v.Weights[4]);
                    importData.VertexData0.Add(v.Weights[1]);
                    importData.VertexData0.Add(v.Weights[5]);
                    importData.VertexData0.Add(v.Weights[2]);
                    importData.VertexData0.Add(v.Weights[6]);
                    importData.VertexData0.Add(v.Weights[3]);
                    importData.VertexData0.Add(v.Weights[7]);

                    importData.VertexData0.Add(v.BoneIds[0]);
                    importData.VertexData0.Add(v.BoneIds[4]);
                    importData.VertexData0.Add(v.BoneIds[1]);
                    importData.VertexData0.Add(v.BoneIds[5]);
                    importData.VertexData0.Add(v.BoneIds[2]);
                    importData.VertexData0.Add(v.BoneIds[6]);
                    importData.VertexData0.Add(v.BoneIds[3]);
                    importData.VertexData0.Add(v.BoneIds[7]);
                } else
                {
                    ModelModifiers.CleanWeight(v, 4, loggingFunction);
                    // 4 byte style ...
                    importData.VertexData0.Add(v.Weights[0]);
                    importData.VertexData0.Add(v.Weights[1]);
                    importData.VertexData0.Add(v.Weights[2]);
                    importData.VertexData0.Add(v.Weights[3]);

                    // Bone Indices
                    importData.VertexData0.Add(v.BoneIds[0]);
                    importData.VertexData0.Add(v.BoneIds[1]);
                    importData.VertexData0.Add(v.BoneIds[2]);
                    importData.VertexData0.Add(v.BoneIds[3]);
                }
            }

            WriteVectorData(importData.VertexData1, vertexInfoList, VertexUsageType.Normal, v);
            WriteVectorData(importData.VertexData1, vertexInfoList, VertexUsageType.Binormal, v);
            WriteVectorData(importData.VertexData1, vertexInfoList, VertexUsageType.Tangent, v);


            if (vertexInfoList.ContainsKey(VertexUsageType.Color))
            {
                importData.VertexData1.Add(v.VertexColor[0]);
                importData.VertexData1.Add(v.VertexColor[1]);
                importData.VertexData1.Add(v.VertexColor[2]);
                importData.VertexData1.Add(v.VertexColor[3]);
            }
            if (vertexInfoList.ContainsKey(VertexUsageType.Color) && vertexInfoList[VertexUsageType.Color].Count > 1)
            {
                // Gee Mom, why does SE let you have two vertex color channels?
                importData.VertexData1.Add(v.VertexColor2[0]);
                importData.VertexData1.Add(v.VertexColor2[1]);
                importData.VertexData1.Add(v.VertexColor2[2]);
                importData.VertexData1.Add(v.VertexColor2[3]);
            }

            // Texture Coordinates
            var texCoordDataType = vertexInfoList[VertexUsageType.TextureCoordinate][0];


            if (texCoordDataType == VertexDataType.Float2 || texCoordDataType == VertexDataType.Float4)
            {
                importData.VertexData1.AddRange(BitConverter.GetBytes(v.UV1[0]));
                importData.VertexData1.AddRange(BitConverter.GetBytes(v.UV1[1]));

                if (texCoordDataType == VertexDataType.Float4)
                {
                    importData.VertexData1.AddRange(BitConverter.GetBytes(v.UV2[0]));
                    importData.VertexData1.AddRange(BitConverter.GetBytes(v.UV2[1]));
                }
            } else if (texCoordDataType == VertexDataType.Half2 || texCoordDataType == VertexDataType.Half4)
            {
                importData.VertexData1.AddRange(BitConverter.GetBytes(((Half)v.UV1[0]).RawValue));
                importData.VertexData1.AddRange(BitConverter.GetBytes(((Half)v.UV1[1]).RawValue));
                if (texCoordDataType == VertexDataType.Half4)
                {
                    importData.VertexData1.AddRange(BitConverter.GetBytes(((Half)v.UV2[0]).RawValue));
                    importData.VertexData1.AddRange(BitConverter.GetBytes(((Half)v.UV2[1]).RawValue));
                }
            }

            var size0 = importData.VertexData0.Count - start0;
            var size1 = importData.VertexData1.Count - start1;
            return size0 + size1;
        }


        /// <summary>
        /// Classes used in reading bone deformation data.
        /// </summary>
        private class DeformationBoneSet
        {
            public List<DeformationBoneData> Data = new List<DeformationBoneData>();
        }
        private class DeformationBoneData
        {
            public string Name;
            public float[] Matrix = new float[16];
        }


        /// <summary>
        /// Loads the deformation files for attempting racial deformation
        /// Currently in debugging phase.
        /// </summary>
        /// <param name="race"></param>
        /// <param name="deformations"></param>
        /// <param name="recalculated"></param>
        public static void GetDeformationMatrices(XivRace race, out Dictionary<string, Matrix> deformations, out Dictionary<string, Matrix> invertedDeformations, out Dictionary<string, Matrix> normalDeformations, out Dictionary<string, Matrix> invertedNormalDeformations)
        {
            deformations = new Dictionary<string, Matrix>();
            invertedDeformations = new Dictionary<string, Matrix>();
            normalDeformations = new Dictionary<string, Matrix>();
            invertedNormalDeformations = new Dictionary<string, Matrix>();


            var deformFile = "Skeletons/c" + race.GetRaceCode() + "_deform.json";
            var deformationLines = File.ReadAllLines(deformFile);
            string deformationJson = deformationLines[0];
            var deformationData = JsonConvert.DeserializeObject<DeformationBoneSet>(deformationJson);
            foreach (var set in deformationData.Data)
            {
                deformations.Add(set.Name, new Matrix(set.Matrix));
            }

            var skelName = "c" + race.GetRaceCode();
            var cwd = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            var skeletonFile = cwd + "/Skeletons/" + skelName + "b0001.skel";

            if (!File.Exists(skeletonFile))
            {
                // Need to extract the Skel file real quick like.
                var tempRoot = new XivDependencyRootInfo();
                tempRoot.PrimaryType = XivItemType.equipment;
                tempRoot.PrimaryId = 0;
                tempRoot.Slot = "top";
                Task.Run(async () =>
                {
                    await Sklb.GetBaseSkeletonFile(tempRoot, race);
                }).Wait();
            }

            var skeletonData = File.ReadAllLines(skeletonFile);
            var FullSkel = new Dictionary<string, SkeletonData>();
            var FullSkelNum = new Dictionary<int, SkeletonData>();

            // Deserializes the json skeleton file and makes 2 dictionaries with names and numbers as keys
            foreach (var b in skeletonData)
            {
                if (b == "") continue;
                var j = JsonConvert.DeserializeObject<SkeletonData>(b);

                FullSkel.Add(j.BoneName, j);
                FullSkelNum.Add(j.BoneNumber, j);
            }

            var root = FullSkel["n_root"];

            BuildNewTransfromMatrices(root, FullSkel, deformations, invertedDeformations, normalDeformations, invertedNormalDeformations);
        }
        private static void BuildNewTransfromMatrices(SkeletonData node, Dictionary<string, SkeletonData> skeletonData, Dictionary<string, Matrix> deformations, Dictionary<string, Matrix> invertedDeformations, Dictionary<string, Matrix> normalDeformations, Dictionary<string, Matrix> invertedNormalDeformations)
        {
            if (node.BoneParent == -1)
            {
                if (!deformations.ContainsKey(node.BoneName))
                {
                    deformations.Add(node.BoneName, Matrix.Identity);
                }
                invertedDeformations.Add(node.BoneName, Matrix.Identity);
                normalDeformations.Add(node.BoneName, Matrix.Identity);
                invertedNormalDeformations.Add(node.BoneName, Matrix.Identity);
            }
            else
            {
                if (deformations.ContainsKey(node.BoneName))
                {
                    invertedDeformations.Add(node.BoneName, deformations[node.BoneName].Inverted());

                    var normalMatrix = deformations[node.BoneName].Inverted();
                    normalMatrix.Transpose();
                    normalDeformations.Add(node.BoneName, normalMatrix);

                    var invertexNormalMatrix = deformations[node.BoneName].Inverted();
                    normalMatrix.Transpose();
                    invertexNormalMatrix.Invert();
                    invertedNormalDeformations.Add(node.BoneName, invertexNormalMatrix);

                }
                else
                {
                    if (!skeletonData.ContainsKey(node.BoneName))
                    {
                        deformations[node.BoneName] = Matrix.Identity;
                        invertedDeformations[node.BoneName] = Matrix.Identity;
                        normalDeformations[node.BoneName] = Matrix.Identity;
                        invertedNormalDeformations[node.BoneName] = Matrix.Identity;
                    }
                    else
                    {
                        var skelEntry = skeletonData[node.BoneName];
                        while (skelEntry != null)
                        {
                            if (deformations.ContainsKey(skelEntry.BoneName))
                            {
                                // This parent has a deform.
                                deformations[node.BoneName] = deformations[skelEntry.BoneName];
                                invertedDeformations[node.BoneName] = invertedDeformations[skelEntry.BoneName];
                                normalDeformations[node.BoneName] = normalDeformations[skelEntry.BoneName];
                                invertedNormalDeformations[node.BoneName] = invertedNormalDeformations[skelEntry.BoneName];
                                break;
                            }

                            // Seek our next parent.
                            skelEntry = skeletonData.FirstOrDefault(x => x.Value.BoneNumber == skelEntry.BoneParent).Value;
                        }

                        if (skelEntry == null)
                        {
                            deformations[node.BoneName] = Matrix.Identity;
                            invertedDeformations[node.BoneName] = Matrix.Identity;
                            normalDeformations[node.BoneName] = Matrix.Identity;
                            invertedNormalDeformations[node.BoneName] = Matrix.Identity;
                        }
                    }
                }
            }
            var children = skeletonData.Where(x => x.Value.BoneParent == node.BoneNumber);
            foreach (var c in children)
            {
                BuildNewTransfromMatrices(c.Value, skeletonData, deformations, invertedDeformations, normalDeformations, invertedNormalDeformations);
            }
        }

        private string _EquipmentModelPathFormat = "chara/equipment/e{0}/model/c{1}e{0}_{2}.mdl";
        private string _AccessoryModelPathFormat = "chara/accessory/a{0}/model/c{1}a{0}_{2}.mdl";

        public static bool IsAutoAssignableModel(string mdlPath)
        {
            if (!mdlPath.StartsWith("chara/"))
            {
                return false;
            }

            if (!mdlPath.EndsWith(".mdl"))
            {
                return false;
            }

            // Ensure Midlander F Based model.
            if (
                (!mdlPath.Contains("c0201"))
                && (!mdlPath.Contains("c0401"))
                && (!mdlPath.Contains("c0601"))
                && (!mdlPath.Contains("c0801"))
                && (!mdlPath.Contains("c1001"))
                && (!mdlPath.Contains("c1401"))
                && (!mdlPath.Contains("c1601"))
                && (!mdlPath.Contains("c1801")))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Performs a heuristic check on the UV data of the given model to determine if its skin material assignment needs to be altered.
        /// Primarily for handling Gen3/Bibo+ compat issues on Female Model skin materials B/D.
        /// </summary>
        /// <param name="mdlPath"></param>
        /// <returns></returns>
        public async Task<bool> CheckSkinAssignment(string mdlPath, IndexFile _index, ModList _modlist)
        {
            if(!IsAutoAssignableModel(mdlPath))
            {
                return false;
            }

            var ogMdl = await GetRawMdlData(mdlPath);
            var ttMdl = TTModel.FromRaw(ogMdl);

            bool anyChanges = false;
            anyChanges = SkinCheckBibo(ttMdl, _index);

            if(!anyChanges)
            {
                anyChanges = SkinCheckAndrofirm(ttMdl);
            }

            if (!anyChanges)
            {
                anyChanges = SkinCheckGen3(ttMdl);
            }


            if (anyChanges)
            {

                var bytes = await MakeCompressedMdlFile(ttMdl, ogMdl);

                // We know by default that a mod entry exists for this file if we're actually doing the check process on it.
                var modEntry = _modlist.Mods.First(x => x.fullPath == mdlPath);
                var _dat = new Dat(XivCache.GameInfo.GameDirectory);
                
                await _dat.WriteModFile(bytes, mdlPath, modEntry.source, null, _index, _modlist, modEntry.modPack);

            }

            return anyChanges;
        }

        /// <summary>
        /// Loops through all mods in the modlist to update their skin assignments, performing a batch save at the end if 
        /// everything was successful.
        /// </summary>
        /// <returns></returns>
        public async Task<int> CheckAllModsSkinAssignments()
        {
            var _modding = new Modding(XivCache.GameInfo.GameDirectory);
            var modList = await _modding.GetModListAsync();

            var _index = new Index(XivCache.GameInfo.GameDirectory);
            var index = await _index.GetIndexFile(XivDataFile._04_Chara);

            int count = 0;
            foreach(var mod in modList.Mods)
            {
                var changed = await CheckSkinAssignment(mod.fullPath, index, modList);
                if(changed)
                {
                    count++;
                }
            }

            if(count > 0)
            {
                await _index.SaveIndexFile(index);
                await _modding.SaveModListAsync(modList);
            }

            return count;
        }
        private bool SkinCheckGen2()
        {
            // If something is on Mat A, we can just assume it's fine realistically to save time.

            // For now this is unneeded as Mat A things are default Gen2, and there are some derivatives of Gen2 on other materials
            // which would complicate this check.
            return false;
        }

        private bool SkinCheckGen3(TTModel ttMdl)
        {
            // Standard forward check.  Primarily this is looking for Mat D materials that are 'gen3 compat patch' for bibo.
            // To pull them back onto mat B.

            // Standard forward check.  Primarily this is looking for standard Mat B bibo materials,
            // To pull them onto mat D.

            var layout = GetUVHashSet("gen3");
            if (layout == null || layout.Count == 0) return false;

            bool anyChanges = false;
            foreach (var mg in ttMdl.MeshGroups)
            {
                var rex = ModelModifiers.SkinMaterialRegex;
                if (rex.IsMatch(mg.Material))
                {
                    var extractRex = new Regex("_([a-z]+)\\.mtrl$");
                    var res = extractRex.Match(mg.Material);
                    if (!res.Success) continue;

                    var matId = res.Groups[1].Value;
                    if (matId != "d") continue;

                    // We have a Material B skin reference in a Hyur F model.

                    var totalVerts = mg.VertexCount;

                    // Take 100 evenly divided samples, or however many we can get if there's not enough verts.
                    uint sampleCount = 100;
                    int sampleDivision = (int)(totalVerts / sampleCount);
                    if (sampleDivision <= 0)
                    {
                        sampleDivision = 1;
                    }

                    uint hits = 0;
                    const float requiredRatio = 0.5f;


                    var realSamples = 0;
                    for (int i = 0; i < totalVerts; i += sampleDivision)
                    {
                        realSamples++;
                        // Get a random vertex.
                        var vert = mg.GetVertexAt(i);

                        var fx = vert.UV1[0];
                        var fy = vert.UV1[1];
                        // Sort quadrant.
                        while (fy < -1)
                        {
                            fy += 1;
                        }
                        while (fy > 0)
                        {
                            fy -= 1;
                        }

                        while (fx > 1)
                        {
                            fx -= 1;
                        }
                        while (fx < 0)
                        {
                            fx += 1;
                        }

                        // This is a simple hash comparison checking if the 
                        // UVs are bytewise idential at half precision.
                        // In the future a better comparison method may be needed,
                        // but this is super fast as it is.
                        var huv = new HalfUV(fx, fy);
                        if (layout.Contains(huv))
                        {
                            hits++;
                        }
                    }

                    float ratio = (float)hits / (float)realSamples;


                    // This Mesh group needs to be swapped.
                    if (ratio >= requiredRatio)
                    {
                        mg.Material = mg.Material.Replace("_" + matId + ".mtrl", "_b.mtrl");
                        anyChanges = true;
                    }
                }
            }
            return anyChanges;
        }

        private bool SkinCheckBibo(TTModel ttMdl, IndexFile _index)
        {
            // Standard forward check.  Primarily this is looking for standard Mat B bibo materials,
            // To pull them onto mat D.

            const string bibo_path = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";
            bool biboPathExists = _index.FileExists(bibo_path);
            var layout = GetUVHashSet("bibo");
            if (layout == null || layout.Count == 0) return false;

            bool anyChanges = false;
            foreach (var mg in ttMdl.MeshGroups)
            {
                var rex = ModelModifiers.SkinMaterialRegex;
                if (rex.IsMatch(mg.Material))
                {
                    var extractRex = new Regex("_([a-z]+)\\.mtrl$");
                    var res = extractRex.Match(mg.Material);
                    if (!res.Success) continue;

                    var matId = res.Groups[1].Value;

                    // We only care if this is a B material model or D material model with a _bibo path to move it to.
                    if(!(matId == "b" || (matId == "d" && biboPathExists)))
                    {
                        continue;
                    }

                    // We have a Material B skin reference in a Hyur F model.

                    var totalVerts = mg.VertexCount;

                    // Take 100 evenly divided samples, or however many we can get if there's not enough verts.
                    uint sampleCount = 100;
                    int sampleDivision = (int)(totalVerts / sampleCount);
                    if (sampleDivision <= 0)
                    {
                        sampleDivision = 1;
                    }

                    uint hits = 0;
                    const float requiredRatio = 0.5f;


                    var realSamples = 0;
                    for (int i = 0; i < totalVerts; i += sampleDivision)
                    {
                        realSamples++;
                        // Get a random vertex.
                        var vert = mg.GetVertexAt(i);

                        var fx = vert.UV1[0];
                        var fy = vert.UV1[1];
                        // Sort quadrant.
                        while (fy < -1)
                        {
                            fy += 1;
                        }
                        while (fy > 0)
                        {
                            fy -= 1;
                        }

                        while (fx > 1)
                        {
                            fx -= 1;
                        }
                        while (fx < 0)
                        {
                            fx += 1;
                        }

                        // This is a simple hash comparison checking if the 
                        // UVs are bytewise idential at half precision.
                        // In the future a better comparison method may be needed,
                        // but this is super fast as it is.
                        var huv = new HalfUV(fx, fy);
                        if (layout.Contains(huv))
                        {
                            hits++;
                        }
                    }

                    float ratio = (float)hits / (float)realSamples;


                    // This Mesh group needs to be swapped.
                    if (ratio >= requiredRatio)
                    {
                        // If the new _bibo material actually exists, move it.
                        if(biboPathExists)
                        {
                            mg.Material = mg.Material.Replace("_" + matId + ".mtrl", "_bibo.mtrl");
                        } else
                        {
                            mg.Material = mg.Material.Replace("_" + matId + ".mtrl", "_d.mtrl");
                        }


                        anyChanges = true;
                    }
                }
            }

            // IF we moved files onto _bibo, we should move any pubes as well onto their pubic hair path.
            if (anyChanges && biboPathExists)
            {
                foreach (var mg in ttMdl.MeshGroups)
                {
                    var rex = ModelModifiers.SkinMaterialRegex;
                    if (rex.IsMatch(mg.Material))
                    {
                        var extractRex = new Regex("_([a-z]+)\\.mtrl$");
                        var res = extractRex.Match(mg.Material);
                        if (!res.Success) continue;

                        var matId = res.Groups[1].Value;
                        if (matId != "c") continue;

                        mg.Material = mg.Material.Replace("_" + matId + ".mtrl", "_bibopube.mtrl");
                    }
                }
            }

            return anyChanges;
        }


        private bool SkinCheckAndrofirm(TTModel ttMdl)
        {
            // AF is a bit of a special case.
            // It's a derivative of Gen2, that only varies on the legs.
            // So if it's anything other than a leg model, we can pass, since it's really just a gen2 model.

            // If it /is/ a leg model though, we have to create a hashset of the 
            // UVs in the material, then reverse check
            // So we have to sample the heuristic data, and see if there are
            // a sufficient amount of matches in the model.

            if (!ttMdl.Source.EndsWith("_dwn.mdl")) return false;

            var layout = GetUVHashSet("androfirm");
            if (layout == null || layout.Count == 0) return false;


            HashSet<HalfUV> modelUVs = new HashSet<HalfUV>();
            List<TTMeshGroup> meshes = new List<TTMeshGroup>();
            bool anyChanges = false;
            foreach (var mg in ttMdl.MeshGroups)
            {
                var rex = ModelModifiers.SkinMaterialRegex;
                if (rex.IsMatch(mg.Material))
                {
                    var extractRex = new Regex("_([a-z]+)\\.mtrl$");
                    var res = extractRex.Match(mg.Material);
                    if (!res.Success) continue;

                    var matId = res.Groups[1].Value;
                    if (matId != "a") continue; // Androfirm was originally published on the A Material.

                    var totalVerts = mg.VertexCount;

                    meshes.Add(mg);

                    for (int i = 0; i < totalVerts; i++)
                    {
                        // Get vertex
                        var vert = mg.GetVertexAt(i);

                        var fx = vert.UV1[0];
                        var fy = vert.UV1[1];

                        // Sort quadrant.
                        while (fy < -1)
                        {
                            fy += 1;
                        }
                        while (fy > 0)
                        {
                            fy -= 1;
                        }

                        while (fx > 1)
                        {
                            fx -= 1;
                        }
                        while (fx < 0)
                        {
                            fx += 1;
                        }

                        // Add to HashSet
                        modelUVs.Add(new HalfUV(fx, fy));
                    }
                }
            }

            if (modelUVs.Count == 0) return false;

            // We have some amount of material A leg UVs.

            var layoutVerts = layout.Count;
            var desiredSamples = 100;
            var skip = layoutVerts / desiredSamples;
            var hits = 0;
            var realSamples = 0;

            // Have to itterate these because can't index access a hashset.
            // maybe cache an array version later if speed proves to be an issue?
            var id = 0;
            foreach(var uv in layout)
            {
                id++;
                if(id % skip == 0)
                {
                    realSamples++;
                    if (modelUVs.Contains(uv))
                    {
                        hits++;
                    }
                }
            }

            float ratio = (float)hits / (float)realSamples;
            const float requiredRatio = 0.5f;

            if(ratio > requiredRatio)
            {
                anyChanges = true;
                foreach(var mesh in meshes)
                {
                    mesh.Material = mesh.Material.Replace("_a.mtrl", "_e.mtrl");
                }
            }

            return anyChanges;
        }

        private bool SkinCheckUNFConnector()
        {
            // Standard forward check.

            // For now this is unneeded, since UNF is the only mod to have been published using the _f material,
            // and has only been published on _f and no other material letter.
            return false;
        }

        /// <summary>
        /// Creates a new racial model for a given set/slot by copying from already existing racial models.
        /// </summary>
        /// <param name="setId"></param>
        /// <param name="slot"></param>
        /// <param name="newRace"></param>
        /// <returns></returns>
        public async Task AddRacialModel(int setId, string slot, XivRace newRace, string source)
        {

            var _index = new Index(_gameDirectory);
            var isAccessory = EquipmentDeformationParameterSet.SlotsAsList(true).Contains(slot);

            if (!isAccessory)
            {
                var slotOk = EquipmentDeformationParameterSet.SlotsAsList(false).Contains(slot);
                if (!slotOk)
                {
                    throw new InvalidDataException("Attempted to get racial models for invalid slot.");
                }
            }

            // If we're adding a new race, we need to clone an existing model, if it doesn't exist already.
            var format = "";
            if (!isAccessory)
            {
                format = _EquipmentModelPathFormat;
            }
            else
            {
                format = _AccessoryModelPathFormat;
            }

            var path = String.Format(format, setId.ToString().PadLeft(4, '0'), newRace.GetRaceCode(), slot);

            // File already exists, no adjustments needed.
            if ((await _index.FileExists(path))) return;

            var _eqp = new Eqp(_gameDirectory);
            var availableModels = await _eqp.GetAvailableRacialModels(setId, slot);
            var baseModelOrder = newRace.GetModelPriorityList();

            // Ok, we need to find which racial model to use as our base now...
            var baseRace = XivRace.All_Races;
            var originalPath = "";
            foreach (var targetRace in baseModelOrder)
            {
                if (availableModels.Contains(targetRace))
                {
                    originalPath = String.Format(format, setId.ToString().PadLeft(4, '0'), targetRace.GetRaceCode(), slot);
                    var exists = await _index.FileExists(originalPath);
                    if (exists)
                    {
                        baseRace = targetRace;
                        break;
                    } else
                    {
                        continue;
                    }
                }
            }

            if (baseRace == XivRace.All_Races) throw new Exception("Unable to find base model to create new racial model from.");

            // Create the new model.
            await CopyModel(originalPath, path, source);
        }

        /// <summary>
        /// Copies a given model from a previous path to a new path, including copying the materials and other down-chain items.
        /// 
        /// </summary>
        /// <param name="originalPath"></param>
        /// <param name="newPath"></param>
        /// <returns></returns>
        public async Task<long> CopyModel(string originalPath, string newPath, string source, bool copyTextures = false)
        {
            var _dat = new Dat(_gameDirectory);
            var _index = new Index(_gameDirectory);
            var _modding = new Modding(_gameDirectory);

            var fromRoot = await XivCache.GetFirstRoot(originalPath);
            var toRoot = await XivCache.GetFirstRoot(newPath);

            IItem item = null;
            if (toRoot != null)
            {
                item = toRoot.GetFirstItem();
            }

            var df = IOUtil.GetDataFileFromPath(originalPath);

            var index = await _index.GetIndexFile(df);
            var modlist = await _modding.GetModListAsync();

            var offset = index.Get8xDataOffset(originalPath);
            var xMdl = await GetRawMdlData(originalPath, false, offset);
            var model = TTModel.FromRaw(xMdl);


            if (model == null)
            {
                throw new InvalidDataException("Source model file does not exist.");
            }
            var allFiles = new HashSet<string>() { newPath };

            var originalRace = IOUtil.GetRaceFromPath(originalPath);
            var newRace = IOUtil.GetRaceFromPath(newPath);


            if(originalRace != newRace)
            {
                // Convert the model to the new race.
                ModelModifiers.RaceConvert(model, originalRace, newPath);
                ModelModifiers.FixUpSkinReferences(model, newPath);
            }

            // Language is irrelevant here.
            var _mtrl = new Mtrl(XivCache.GameInfo.GameDirectory);

            // Get all variant materials.
            var materialPaths = await GetReferencedMaterialPaths(originalPath, -1, false, false, index, modlist);

            
            var _raceRegex = new Regex("c[0-9]{4}");

            Dictionary<string, string> validNewMaterials = new Dictionary<string, string>();
            HashSet<string> copiedPaths = new HashSet<string>();
            // Update Material References and clone materials.
            foreach (var material in materialPaths)
            {

                // Get the new path.
                var path = RootCloner.UpdatePath(fromRoot, toRoot, material);

                // Adjust race code entries if needed.
                if (toRoot.Info.PrimaryType == XivItemType.equipment || toRoot.Info.PrimaryType == XivItemType.accessory)
                {
                    path = _raceRegex.Replace(path, "c" + newRace.GetRaceCode());
                }

                // Get file names.
                var io = material.LastIndexOf("/", StringComparison.Ordinal);
                var originalMatName = material.Substring(io, material.Length - io);

                io = path.LastIndexOf("/", StringComparison.Ordinal);
                var newMatName = path.Substring(io, path.Length - io);


                // Time to copy the materials!
                try
                {
                    offset = index.Get8xDataOffset(material);
                    var mtrl = await _mtrl.GetMtrlData(offset, material);

                    if (copyTextures)
                    {
                        for(int i = 0; i < mtrl.Textures.Count; i++)
                        {
                            var tex = mtrl.Textures[i].TexturePath;
                            var ntex = RootCloner.UpdatePath(fromRoot, toRoot, tex);
                            if (toRoot.Info.PrimaryType == XivItemType.equipment || toRoot.Info.PrimaryType == XivItemType.accessory)
                            {
                                ntex = _raceRegex.Replace(ntex, "c" + newRace.GetRaceCode());
                            }

                            mtrl.Textures[i].TexturePath = ntex;

                            allFiles.Add(ntex);
                            await _dat.CopyFile(tex, ntex, source, true, item, index, modlist);
                        }
                    }

                    mtrl.MTRLPath = path;
                    allFiles.Add(mtrl.MTRLPath);
                    await _mtrl.ImportMtrl(mtrl, item, source, index, modlist);

                    if(!validNewMaterials.ContainsKey(newMatName))
                    {
                        validNewMaterials.Add(newMatName, path);
                    }
                    copiedPaths.Add(path);


                    // Switch out any material references to the material in the model file.
                    foreach (var m in model.MeshGroups)
                    {
                        if(m.Material == originalMatName)
                        {
                            m.Material = newMatName;
                        }
                    }

                } catch(Exception ex)
                {
                    // Hmmm.  The original material didn't exist.   This is pretty not awesome, but I guess a non-critical error...?
                }
            }

            if (Imc.UsesImc(toRoot) && Imc.UsesImc(fromRoot))
            {
                var _imc = new Imc(XivCache.GameInfo.GameDirectory);

                var toEntries = await _imc.GetEntries(await toRoot.GetImcEntryPaths(), false, index, modlist);
                var fromEntries = await _imc.GetEntries(await fromRoot.GetImcEntryPaths(), false, index, modlist);

                var toSets = toEntries.Select(x => x.MaterialSet).Where(x => x != 0).ToList();
                var fromSets = fromEntries.Select(x => x.MaterialSet).Where(x => x != 0).ToList();

                if(fromSets.Count > 0 && toSets.Count > 0)
                {
                    var vReplace = new Regex("/v[0-9]{4}/");

                    // Validate that sufficient material sets have been created at the destination root.
                    foreach(var mkv in validNewMaterials)
                    {
                        var validPath = mkv.Value;
                        foreach(var msetId in toSets)
                        {
                            var testPath = vReplace.Replace(validPath, "/v" + msetId.ToString().PadLeft(4, '0') + "/");
                            var copied = copiedPaths.Contains(testPath);

                            // Missing a material set, copy in the known valid material.
                            if(!copied)
                            {
                                allFiles.Add(testPath);
                                await _dat.CopyFile(validPath, testPath, source, true, item, index, modlist);
                            }
                        }
                    }
                }
            }


            // Save the final modified mdl.
            var data = await MakeCompressedMdlFile(model, xMdl);
            offset = await _dat.WriteModFile(data, newPath, source, item, index, modlist);

            await _index.SaveIndexFile(index);
            await _modding.SaveModListAsync(modlist);
            XivCache.QueueDependencyUpdate(allFiles.ToList());

            return offset;
        }


        public async Task FixPreDawntrailMdl(string path, string source = "Unknown")
        {
            var _dat = new Dat(XivCache.GameInfo.GameDirectory);

            // HACKHACK: This is going to be extremely inefficient, but works for the moment.
            var ttMdl = await GetModel(path);
            var xivMdl = await GetRawMdlData(path);

            var bytes = await MakeCompressedMdlFile(ttMdl, xivMdl);
            await _dat.WriteModFile(bytes, path, source);


        }

        /// <summary>
        /// Gets the MDL path
        /// </summary>
        /// <param name="itemModel">The item model</param>
        /// <param name="xivRace">The selected race for the given item</param>
        /// <param name="submeshId">The submesh ID - Only used for furniture items which contain multiple meshes, like the Ahriman Clock.</param>
        /// <returns>The path in string format.  Not a fucking tuple.</returns>
        public async Task<string> GetMdlPath(IItemModel itemModel, XivRace xivRace, string submeshId = null)
        {
            string mdlFolder = "", mdlFile = "";

            var mdlInfo = itemModel.ModelInfo;
            var id = mdlInfo.PrimaryID.ToString().PadLeft(4, '0');
            var bodyVer = mdlInfo.SecondaryID.ToString().PadLeft(4, '0');
            var itemCategory = itemModel.SecondaryCategory;

            var race = xivRace.GetRaceCode();
            var itemType = itemModel.GetPrimaryItemType();

            switch (itemType)
            {
                case XivItemType.equipment:
                    mdlFolder = $"chara/{itemType}/e{id}/model";
                    mdlFile = $"c{race}e{id}_{itemModel.GetItemSlotAbbreviation()}{MdlExtension}";
                    break;
                case XivItemType.accessory:
                    mdlFolder = $"chara/{itemType}/a{id}/model";
                    var abrv = itemModel.GetItemSlotAbbreviation();
                    // Just left ring things.
                    if (submeshId == "ril")
                    {
                        abrv = "ril";
                    }
                    mdlFile = $"c{race}a{id}_{abrv}{MdlExtension}";
                    break;
                case XivItemType.weapon:
                    mdlFolder = $"chara/{itemType}/w{id}/obj/body/b{bodyVer}/model";
                    mdlFile = $"w{id}b{bodyVer}{MdlExtension}";
                    break;
                case XivItemType.monster:
                    mdlFolder = $"chara/{itemType}/m{id}/obj/body/b{bodyVer}/model";
                    mdlFile = $"m{id}b{bodyVer}{MdlExtension}";
                    break;
                case XivItemType.demihuman:
                    mdlFolder = $"chara/{itemType}/d{id}/obj/equipment/e{bodyVer}/model";
                    mdlFile = $"d{id}e{bodyVer}_{SlotAbbreviationDictionary[itemModel.TertiaryCategory]}{MdlExtension}";
                    break;
                case XivItemType.human:
                    if (itemCategory.Equals(XivStrings.Body))
                    {
                        mdlFolder = $"chara/{itemType}/c{race}/obj/body/b{bodyVer}/model";
                        mdlFile = $"c{race}b{bodyVer}_{SlotAbbreviationDictionary[itemModel.TertiaryCategory]}{MdlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Hair))
                    {
                        mdlFolder = $"chara/{itemType}/c{race}/obj/hair/h{bodyVer}/model";
                        mdlFile = $"c{race}h{bodyVer}_{SlotAbbreviationDictionary[itemCategory]}{MdlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Face))
                    {
                        mdlFolder = $"chara/{itemType}/c{race}/obj/face/f{bodyVer}/model";
                        mdlFile = $"c{race}f{bodyVer}_{SlotAbbreviationDictionary[itemCategory]}{MdlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Tail))
                    {
                        mdlFolder = $"chara/{itemType}/c{race}/obj/tail/t{bodyVer}/model";
                        mdlFile = $"c{race}t{bodyVer}_{SlotAbbreviationDictionary[itemCategory]}{MdlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Ear))
                    {
                        mdlFolder = $"chara/{itemType}/c{race}/obj/zear/z{bodyVer}/model";
                        mdlFile = $"c{race}z{bodyVer}_zer{MdlExtension}";
                    }
                    break;
                case XivItemType.furniture:
                    // Language doesn't matter for this call.
                    var housing = new Housing(_gameDirectory, XivLanguage.None);
                    var mdlPath = "";
                    // Housing assets use a different function to scrub the .sgd files for
                    // their direct absolute model references.
                    var assetDict = await housing.GetFurnitureModelParts(itemModel);

                    if (submeshId == null || submeshId == "base")
                    {
                        submeshId = "b0";
                    }

                    mdlPath = assetDict[submeshId];
                    return mdlPath;
                    break;
                default:
                    mdlFolder = "";
                    mdlFile = "";
                    break;
            }

            return mdlFolder + "/" + mdlFile;
        }

        public static readonly Dictionary<string, string> SlotAbbreviationDictionary = new Dictionary<string, string>
        {
            {XivStrings.Head, "met"},
            {XivStrings.Hands, "glv"},
            {XivStrings.Legs, "dwn"},
            {XivStrings.Feet, "sho"},
            {XivStrings.Body, "top"},
            {XivStrings.Earring, "ear"},
            {XivStrings.Ear, "zer"},
            {XivStrings.Neck, "nek"},
            {XivStrings.Rings, "rir"},
            {XivStrings.LeftRing, "ril"},
            {XivStrings.Wrists, "wrs"},
            {XivStrings.Head_Body, "top"},
            {XivStrings.Body_Hands, "top"},
            {XivStrings.Body_Hands_Legs, "top"},
            {XivStrings.Body_Legs_Feet, "top"},
            {XivStrings.Body_Hands_Legs_Feet, "top"},
            {XivStrings.Legs_Feet, "dwn"},
            {XivStrings.All, "top"},
            {XivStrings.Face, "fac"},
            {XivStrings.Iris, "iri"},
            {XivStrings.Etc, "etc"},
            {XivStrings.Accessory, "acc"},
            {XivStrings.Hair, "hir"},
            {XivStrings.Tail, "til"},

        };

        private static readonly Dictionary<byte, VertexDataType> VertexTypeDictionary =
            new Dictionary<byte, VertexDataType>
            {
                {0x0, VertexDataType.Float1},
                {0x1, VertexDataType.Float2},
                {0x2, VertexDataType.Float3},
                {0x3, VertexDataType.Float4},
                {0x5, VertexDataType.Ubyte4},
                {0x6, VertexDataType.Short2},
                {0x7, VertexDataType.Short4},
                {0x8, VertexDataType.Ubyte4n},
                {0x9, VertexDataType.Short2n},
                {0xA, VertexDataType.Short4n},
                {0xD, VertexDataType.Half2},    // These do not match D3DDECLUSAGE because SE uses their own translation table.
                {0xE, VertexDataType.Half4},    // Do not change or things will break.
                //{0xF, VertexDataType.Half2},
                //{0x10, VertexDataType.Half4},
                {0x11, VertexDataType.UByte8} // XXX: Bone weights use this type, not sure what it is
            };

        private static readonly Dictionary<byte, VertexUsageType> VertexUsageDictionary =
            new Dictionary<byte, VertexUsageType>
            {
                {0x0, VertexUsageType.Position },
                {0x1, VertexUsageType.BoneWeight },
                {0x2, VertexUsageType.BoneIndex },
                {0x3, VertexUsageType.Normal },
                {0x4, VertexUsageType.TextureCoordinate },
                {0x5, VertexUsageType.Tangent },
                {0x6, VertexUsageType.Binormal },
                {0x7, VertexUsageType.Color }
            };

        private class ColladaMeshData
        {
            public MeshGeometry3D MeshGeometry { get; set; }

            public List<byte[]> BoneIndices { get; set; }

            public List<byte[]> BoneWeights { get; set; }

            public List<int> Handedness { get; set; } = new List<int>();

            public Vector2Collection TextureCoordintes1 { get; set; }

            public Vector3Collection VertexColors { get; set; } = new Vector3Collection();

            public List<float> VertexAlphas { get; set; } = new List<float>();

            public Dictionary<int, int> PartsDictionary { get; set; }

            public Dictionary<int, List<string>> PartBoneDictionary { get; set; } = new Dictionary<int, List<string>>();

            public Dictionary<string, int> BoneNumDictionary;
        }

        /// <summary>
        /// This class holds the imported data after its been converted to bytes
        /// </summary>
        private class VertexByteData
        {
            public List<byte> VertexData0 { get; set; } = new List<byte>();

            public List<byte> VertexData1 { get; set; } = new List<byte>();

            public List<byte> VertexData2 { get; set; } = new List<byte>();

            public List<byte> IndexData { get; set; } = new List<byte>();

            public int VertexCount { get; set; }

            public int IndexCount { get; set; }
        }

        private class VertexDataSection
        {
            public int CompressedVertexDataBlockSize { get; set; }

            public int CompressedIndexDataBlockSize { get; set; }

            public int VertexDataBlockPartCount { get; set; }

            public int IndexDataBlockPartCount { get; set; }

            public List<byte> VertexDataBlock = new List<byte>();

            public List<byte> IndexDataBlock = new List<byte>();
        }
    }
}
