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
using System.Threading;
using System.Threading.Tasks;
using xivModdingFramework.General.Enums;
using xivModdingFramework.Helpers;
using xivModdingFramework.Items;
using xivModdingFramework.Items.DataContainers;
using xivModdingFramework.Items.Enums;
using xivModdingFramework.Items.Interfaces;
using xivModdingFramework.Materials.DataContainers;
using xivModdingFramework.Resources;
using xivModdingFramework.SqPack.FileTypes;
using xivModdingFramework.Textures.DataContainers;
using xivModdingFramework.Textures.Enums;
using xivModdingFramework.Textures.FileTypes;
using xivModdingFramework.Variants.FileTypes;

namespace xivModdingFramework.Materials.FileTypes
{
    /// <summary>
    /// This class contains the methods that deal with the .mtrl file type 
    /// </summary>
    public class Mtrl
    {
        private const string MtrlExtension = ".mtrl";
        private readonly DirectoryInfo _gameDirectory;
        private readonly XivLanguage _language;
        private XivDataFile _dataFile;
        private static SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);

        public Mtrl(DirectoryInfo gameDirectory, XivDataFile dataFile, XivLanguage lang)
        {
            _gameDirectory = gameDirectory;
            _language = lang;
            DataFile = dataFile;
        }

        public XivDataFile DataFile
        {
            get => _dataFile;
            set => _dataFile = value;
        }


        // MtrlParam constants for texture types.
        public static Dictionary<XivTexType, uint> TextureDescriptorValues = new Dictionary<XivTexType, uint>()
        {
            { XivTexType.Normal, 207536625 },
            { XivTexType.Diffuse, 290653886 },
            { XivTexType.Specular, 731504677 },
            { XivTexType.Multi, 2320401078 },
            { XivTexType.Reflection, 4271961042 }   // Used for the Catchlight texture in Iris Materials.
        };

        // MtrlParam constants for file compressions/formats.
        public static Dictionary<MtrlTextureDescriptorFormat, short> TextureDescriptorFormatValues = new Dictionary<MtrlTextureDescriptorFormat, short>()
        {
            { MtrlTextureDescriptorFormat.UsesColorset, -32768 }, // There is some variation on these values, but it always occures in the last 6 bits, and doesn't seem
            { MtrlTextureDescriptorFormat.NoColorset, -31936 }  // To have an appreciable change (Either [000000] or [010101])
            // Non-normal maps are always [WithoutAlpha].  Normal maps are always [WithAlpha], 
            // with the exception that 8.8.8.8 ARGB normal maps use the WithoutAlpha flag (And do not pull a colorset).

            // In the case of faces, the first bit is also flipped to 0 for the normal maps.  Unknown why.
        };


        // Data for setting up MTRL shaders.  Taken from SE samples.  Seems to be consistent across each type of setup.
        // The exact order of these structs does not seem to be important, only the data in question.
        // It may be viable (though inefficient) to simply include all of them.
        public static Dictionary<XivTexType, TextureUsageStruct> TextureUsageValues = new Dictionary<XivTexType, TextureUsageStruct>()
        {
            { XivTexType.Normal, new TextureUsageStruct() { TextureType = 4113354501, Unknown = 2815623008 } },
            { XivTexType.Multi, new TextureUsageStruct() { TextureType = 3531043187, Unknown = 4083110193 } },
            { XivTexType.Diffuse, new TextureUsageStruct() { TextureType = 3054951514, Unknown = 1611594207 } },
            { XivTexType.Specular, new TextureUsageStruct() { TextureType = 3367837167, Unknown = 2687453224 } },
        };

        
        // References to the additional shader data at the end of MTRL files.
        // Stored by ID, though unknown yet what the IDs correspond to.
        // I suspect the IDs are used for piping additional maps/values to the shader, such as cube maps for reflection
        // and skin/hair/iris color, etc. -Sel
        // The actual byte array values do change a bit, but thus far I haven't been able to notice differences when they're changed.
        public static Dictionary<MtrlShaderParameterId, List<byte>> ShaderParameterValues = new Dictionary<MtrlShaderParameterId, List<byte>>() {
            { MtrlShaderParameterId.Common1, new List<byte>(){ 0,0,0,63 } },
            { MtrlShaderParameterId.Common2, new List<byte>(){ 0,0,128,63 } },
            { MtrlShaderParameterId.Skin1, new List<byte>(){ 51, 51, 179, 63, 51, 51, 179, 63, 51, 51, 179, 63 } },
            { MtrlShaderParameterId.Skin2, new List<byte>(){ 0,0,128,63 } },
            { MtrlShaderParameterId.Skin3, new List<byte>(){ 0,0,64,64 } },
            { MtrlShaderParameterId.Skin4, new List<byte>(){ 10, 215, 163, 60, 10, 215, 163, 60, 10, 215, 163, 60 } },
            { MtrlShaderParameterId.Skin5, new List<byte>(){ 205, 204, 204, 62, 205, 204, 204, 62, 205, 204, 204, 62 } },
            { MtrlShaderParameterId.Skin6, new List<byte>(){ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } },
            { MtrlShaderParameterId.Skin7, new List<byte>(){ 0, 0, 130, 66, 0, 0, 200, 66 } },
            { MtrlShaderParameterId.Skin8, new List<byte>(){ 0, 0, 124, 66 } },
            { MtrlShaderParameterId.Equipment1, new List<byte>(){ 0, 0, 0, 0 } },
            { MtrlShaderParameterId.Face1, new List<byte>(){ 0, 0, 0, 66 } },
            { MtrlShaderParameterId.Hair1, new List<byte>(){ 51, 51, 179, 62 } },
            { MtrlShaderParameterId.Hair2, new List<byte>(){ 0, 0, 0, 63 } },


            { MtrlShaderParameterId.Furniture1, new List<byte>(){ 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63 } },
            { MtrlShaderParameterId.Furniture2, new List<byte>(){ 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63 } },
            { MtrlShaderParameterId.Furniture3, new List<byte>(){ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } },
            { MtrlShaderParameterId.Furniture4, new List<byte>(){ 0, 0, 128, 63 } },
            { MtrlShaderParameterId.Furniture5, new List<byte>(){ 143, 194, 117, 60 } },
            { MtrlShaderParameterId.Furniture6, new List<byte>(){ 143, 194, 117, 60 } },
            { MtrlShaderParameterId.Furniture7, new List<byte>(){ 0, 0, 128, 63 } },
            { MtrlShaderParameterId.Furniture8, new List<byte>(){ 0, 0, 128, 63 } },
            { MtrlShaderParameterId.Furniture9, new List<byte>(){ 0, 0, 128, 63 } },
            { MtrlShaderParameterId.Furniture10, new List<byte>(){ 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63 } },
            { MtrlShaderParameterId.Furniture11, new List<byte>(){ 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63 } },
            { MtrlShaderParameterId.Furniture12, new List<byte>(){ 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63 } },
            { MtrlShaderParameterId.Furniture13, new List<byte>(){ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63 } },
        };



        /// <summary>
        /// Gets the MTRL data for the given item 
        /// </summary>
        /// <remarks>
        /// It requires a race (The default is usually <see cref="XivRace.Hyur_Midlander_Male"/>)
        /// It also requires an mtrl part <see cref="GearInfo.GetPartList(IItemModel, XivRace)"/> (default is 'a')
        /// </remarks>
        /// <param name="itemModel">Item that contains model data</param>
        /// <param name="race">The race for the requested data</param>
        /// <param name="part">The Mtrl part </param>
        /// <returns>XivMtrl containing all the mtrl data</returns>
        public async Task<XivMtrl> GetMtrlData(IItemModel itemModel, XivRace race, char part, int dxVersion, string type = "Primary")
        {
            var index = new Index(_gameDirectory);
            var itemType = ItemType.GetItemType(itemModel);

            // Get mtrl path
            var mtrlPath = await GetMtrlPath(itemModel, race, part, itemType, type);
            var mtrlStringPath = $"{mtrlPath.Folder}/{mtrlPath.File}";

            // Get mtrl offset
            var mtrlOffset = await index.GetDataOffset(HashGenerator.GetHash(mtrlPath.Folder),
                HashGenerator.GetHash(mtrlPath.File),
                DataFile);

            if (mtrlOffset == 0 && itemType == XivItemType.furniture)
            {
                mtrlPath.File = mtrlPath.File.Replace("_0", "_1");
                mtrlStringPath = $"{mtrlPath.Folder}/{mtrlPath.File}";

                // Get mtrl offset
                mtrlOffset = await index.GetDataOffset(HashGenerator.GetHash(mtrlPath.Folder),
                    HashGenerator.GetHash(mtrlPath.File),
                    DataFile);
            }

            if (mtrlOffset == 0)
            {
                // If secondary info does not give an offset, try setting body to 1
                if (type.Equals("Secondary"))
                {
                    var xivGear = itemModel as XivGear;
                    // Get mtrl path
                    var originalBody = xivGear.SecondaryModelInfo.Body;
                    xivGear.SecondaryModelInfo.Body = 1;

                    if (xivGear.SecondaryModelInfo.ModelID > 8800 && xivGear.SecondaryModelInfo.ModelID < 8900)
                    {
                        itemType = XivItemType.equipment;
                        race = XivRace.Hyur_Midlander_Male;
                    }

                    mtrlPath = await GetMtrlPath(itemModel, race, part, itemType, type);
                    mtrlStringPath = $"{mtrlPath.Folder}/{mtrlPath.File}";

                    // Get mtrl offset
                    mtrlOffset = await index.GetDataOffset(HashGenerator.GetHash(mtrlPath.Folder),
                        HashGenerator.GetHash(mtrlPath.File),
                        DataFile);

                    // If secondary info with body at 1 does not give an offset, go back to primary
                    if (mtrlOffset == 0)
                    {
                        // Get mtrl path
                        mtrlPath = await GetMtrlPath(itemModel, race, part, itemType, "Primary");
                        mtrlStringPath = $"{mtrlPath.Folder}/{mtrlPath.File}";

                        // Get mtrl offset
                        mtrlOffset = await index.GetDataOffset(HashGenerator.GetHash(mtrlPath.Folder),
                            HashGenerator.GetHash(mtrlPath.File),
                            DataFile);

                        if (mtrlOffset == 0)
                        {
                            throw new Exception($"Could not find offset for {mtrlStringPath}");
                        }
                    }
                }
                else
                {
                    throw new Exception($"Could not find offset for {mtrlStringPath}");
                }
            }

            var mtrlData = await GetMtrlData(mtrlOffset, mtrlStringPath, dxVersion);

            if (mtrlPath.HasVfx)
            {
                var atex = new ATex(_gameDirectory, DataFile);
                try
                {
                    mtrlData.TextureTypePathList.AddRange(await atex.GetAtexPaths(itemModel));
                }
                catch
                {
                    return mtrlData;
                }                
            }

            return mtrlData;
        }

        /// <summary>
        /// Gets the MTRL data for the given item 
        /// </summary>
        /// <remarks>
        /// It requires a race (The default is usually <see cref="XivRace.Hyur_Midlander_Male"/>)
        /// It also requires an mtrl part <see cref="GearInfo.GetPartList(IItemModel, XivRace)"/> (default is 'a')
        /// </remarks>
        /// <param name="itemModel">Item that contains model data</param>
        /// <param name="race">The race for the requested data</param>
        /// <param name="mtrlFile">The Mtrl file</param>
        /// <returns>XivMtrl containing all the mtrl data</returns>
        public async Task<XivMtrl> GetMtrlData(IItemModel itemModel, XivRace race, string mtrlFile, int dxVersion)
        {
            var index = new Index(_gameDirectory);
            var itemType = ItemType.GetItemType(itemModel);

            // Secondary model is gear if between 8800 and 8900 instead of weapon
            if (itemModel.ModelInfo.ModelID > 8800 && itemModel.ModelInfo.ModelID < 8900)
            {
                itemType = XivItemType.equipment;
            }

            // Get mtrl path
            var mtrlFolder = await GetMtrlFolder(itemModel, race, itemType);
            var mtrlStringPath = $"{mtrlFolder}/{mtrlFile}";

            if (itemType == XivItemType.furniture)
            {
                mtrlStringPath = $"b{mtrlFile}";
                mtrlFolder = Path.GetDirectoryName(mtrlStringPath).Replace("\\", "/");
                mtrlFile = Path.GetFileName(mtrlStringPath);
            }

            // Get mtrl offset
            var mtrlOffset = await index.GetDataOffset(HashGenerator.GetHash(mtrlFolder), HashGenerator.GetHash(mtrlFile),
                DataFile);
            if (mtrlOffset == 0)
            {
                // Change to default version if no offset is found and try to get offset again
                if (!mtrlFolder.Contains("v0001"))
                {
                    var newMtrlFolder = mtrlFolder.Substring(0, mtrlFolder.LastIndexOf("v")) + "v0001";

                    mtrlOffset = await index.GetDataOffset(HashGenerator.GetHash(newMtrlFolder), HashGenerator.GetHash(mtrlFile), DataFile);

                    if (mtrlOffset == 0)
                    {
                        throw new Exception($"Could not find offset for {mtrlStringPath}");
                    }

                    mtrlStringPath = $"{newMtrlFolder}/{mtrlFile}";
                }
                else
                {
                    throw new Exception($"Could not find offset for {mtrlStringPath}");

                }
            }

            var mtrlData = await GetMtrlData(mtrlOffset, mtrlStringPath, dxVersion);

            return mtrlData;
        }

        /// <summary>
        /// Gets the MTRL data for the given offset and path
        /// </summary>
        /// <param name="mtrlOffset">The offset to the mtrl in the dat file</param>
        /// <param name="mtrlPath">The full internal game path for the mtrl</param>
        /// <returns>XivMtrl containing all the mtrl data</returns>
        public async Task<XivMtrl> GetMtrlData(int mtrlOffset, string mtrlPath, int dxVersion)
        {
            var dat = new Dat(_gameDirectory);
            var index = new Index(_gameDirectory);

            // Get uncompressed mtrl data
            var mtrlData = await dat.GetType2Data(mtrlOffset, DataFile);

            XivMtrl xivMtrl = null;

            await _semaphoreSlim.WaitAsync();

            try
            {
                await Task.Run((Func<Task>)(async () =>
                {
                    using (var br = new BinaryReader(new MemoryStream(mtrlData)))
                    {
                        xivMtrl = new XivMtrl
                        {
                            Signature = br.ReadInt32(),
                            FileSize = br.ReadInt16(),
                            ColorSetDataSize = br.ReadUInt16(),
                            MaterialDataSize = br.ReadUInt16(),
                            TexturePathsDataSize = br.ReadUInt16(),
                            TextureCount = br.ReadByte(),
                            MapCount = br.ReadByte(),
                            ColorSetCount = br.ReadByte(),
                            UnknownDataSize = br.ReadByte(),
                            TextureTypePathList = new List<TexTypePath>(),
                            MTRLPath = mtrlPath
                        };

                        var pathSizeList = new List<int>();

                        // get the texture path offsets
                        xivMtrl.TexturePathOffsetList = new List<int>(xivMtrl.TextureCount);
                        xivMtrl.TexturePathUnknownList = new List<short>(xivMtrl.TextureCount);
                        for (var i = 0; i < xivMtrl.TextureCount; i++)
                        {
                            xivMtrl.TexturePathOffsetList.Add(br.ReadInt16());
                            xivMtrl.TexturePathUnknownList.Add(br.ReadInt16());

                            // add the size of the paths
                            if (i > 0)
                            {
                                pathSizeList.Add(
                                    xivMtrl.TexturePathOffsetList[i] - xivMtrl.TexturePathOffsetList[i - 1]);
                            }
                        }

                        // get the map path offsets
                        xivMtrl.MapPathOffsetList = new List<int>(xivMtrl.MapCount);
                        xivMtrl.MapPathUnknownList = new List<short>(xivMtrl.MapCount);
                        for (var i = 0; i < xivMtrl.MapCount; i++)
                        {
                            xivMtrl.MapPathOffsetList.Add(br.ReadInt16());
                            xivMtrl.MapPathUnknownList.Add(br.ReadInt16());

                            // add the size of the paths
                            if (i > 0)
                            {
                                pathSizeList.Add(xivMtrl.MapPathOffsetList[i] - xivMtrl.MapPathOffsetList[i - 1]);
                            }
                            else
                            {
                                if (xivMtrl.TextureCount > 0)
                                {
                                    pathSizeList.Add(xivMtrl.MapPathOffsetList[i] -
                                                     xivMtrl.TexturePathOffsetList[xivMtrl.TextureCount - 1]);
                                }
                            }
                        }

                        // get the color set offsets
                        xivMtrl.ColorSetPathOffsetList = new List<int>(xivMtrl.ColorSetCount);
                        xivMtrl.ColorSetPathUnknownList = new List<short>(xivMtrl.ColorSetCount);
                        for (var i = 0; i < xivMtrl.ColorSetCount; i++)
                        {
                            xivMtrl.ColorSetPathOffsetList.Add(br.ReadInt16());
                            xivMtrl.ColorSetPathUnknownList.Add(br.ReadInt16());

                            // add the size of the paths
                            if (i > 0)
                            {
                                pathSizeList.Add(xivMtrl.ColorSetPathOffsetList[i] -
                                                 xivMtrl.ColorSetPathOffsetList[i - 1]);
                            }
                            else
                            {
                                pathSizeList.Add(xivMtrl.ColorSetPathOffsetList[i] -
                                                 xivMtrl.MapPathOffsetList[xivMtrl.MapCount - 1]);
                            }
                        }

                        pathSizeList.Add(xivMtrl.TexturePathsDataSize -
                                         xivMtrl.ColorSetPathOffsetList[xivMtrl.ColorSetCount - 1]);

                        var count = 0;

                        // get the texture path strings
                        xivMtrl.TexturePathList = new List<string>(xivMtrl.TextureCount);
                        for (var i = 0; i < xivMtrl.TextureCount; i++)
                        {
                            var texturePath = Encoding.UTF8.GetString(br.ReadBytes(pathSizeList[count]))
                                .Replace("\0", "");
                            var dx11FileName = Path.GetFileName(texturePath).Insert(0, "--");

                            if (await index.FileExists(HashGenerator.GetHash(dx11FileName),
                                HashGenerator.GetHash(Path.GetDirectoryName(texturePath).Replace("\\", "/")),
                                DataFile))
                            {
                                texturePath = texturePath.Insert(texturePath.LastIndexOf("/") + 1, "--");
                            }

                            xivMtrl.TexturePathList.Add(texturePath);
                            count++;
                        }

                        // add the textures to the TextureTypePathList
                        xivMtrl.TextureTypePathList.AddRange(await GetTexNames(xivMtrl.TexturePathList, DataFile));

                        // get the map path strings
                        xivMtrl.MapPathList = new List<string>(xivMtrl.MapCount);
                        for (var i = 0; i < xivMtrl.MapCount; i++)
                        {
                            xivMtrl.MapPathList.Add(Encoding.UTF8.GetString(br.ReadBytes(pathSizeList[count]))
                                .Replace("\0", ""));
                            count++;
                        }

                        // get the color set path strings
                        xivMtrl.ColorSetPathList = new List<string>(xivMtrl.ColorSetCount);
                        for (var i = 0; i < xivMtrl.ColorSetCount; i++)
                        {
                            xivMtrl.ColorSetPathList.Add(Encoding.UTF8.GetString(br.ReadBytes(pathSizeList[count]))
                                .Replace("\0", ""));
                            count++;
                        }

                        // If the mtrl file contains a color set, add it to the TextureTypePathList
                        if (xivMtrl.ColorSetDataSize > 0)
                        {
                            var ttp = new TexTypePath
                            {
                                Path = mtrlPath,
                                Type = XivTexType.ColorSet,
                                DataFile = DataFile
                            };
                            xivMtrl.TextureTypePathList.Add(ttp);
                        }

                        var shaderPathSize = xivMtrl.MaterialDataSize - xivMtrl.TexturePathsDataSize;

                        xivMtrl.Shader = Encoding.UTF8.GetString(br.ReadBytes(shaderPathSize)).Replace("\0", "");

                        xivMtrl.Unknown2 = br.ReadBytes(xivMtrl.UnknownDataSize);

                        xivMtrl.ColorSetData = new List<Half>();
                        xivMtrl.ColorSetExtraData = null;
                        if (xivMtrl.ColorSetDataSize > 0)
                        {
                            // Color Data is always 512 (6 x 14 = 64 x 8bpp = 512)
                            var colorDataSize = 512;

                            for (var i = 0; i < colorDataSize / 2; i++)
                            {
                                xivMtrl.ColorSetData.Add(new Half(br.ReadUInt16()));
                            }

                            // If the color set is 544 in length, it has an extra 32 bytes at the end
                            if (xivMtrl.ColorSetDataSize == 544)
                            {
                                xivMtrl.ColorSetExtraData = br.ReadBytes(32);
                            }
                        }

                        var originalShaderParameterDataSize = br.ReadUInt16();

                        var originalTextureUsageCount = br.ReadUInt16();

                        var originalShaderParameterCount = br.ReadUInt16();

                        var originalTextureDescriptorCount = br.ReadUInt16();

                        xivMtrl.ShaderNumber = br.ReadUInt16();

                        xivMtrl.Unknown3 = br.ReadUInt16();

                        xivMtrl.TextureUsageList = new List<TextureUsageStruct>((int)originalTextureUsageCount);
                        for (var i = 0; i < originalTextureUsageCount; i++)
                        {
                            xivMtrl.TextureUsageList.Add(new TextureUsageStruct
                            {
                                TextureType = br.ReadUInt32(), Unknown = br.ReadUInt32()});
                        }

                        xivMtrl.ShaderParameterList = new List<ShaderParameterStruct>(originalShaderParameterCount);
                        for (var i = 0; i < originalShaderParameterCount; i++)
                        {
                            xivMtrl.ShaderParameterList.Add(new ShaderParameterStruct
                            {
                                ParameterID = (MtrlShaderParameterId) br.ReadUInt32(), Offset = br.ReadInt16(), Size = br.ReadInt16()});
                        }

                        xivMtrl.TextureDescriptorList = new List<TextureDescriptorStruct>(originalTextureDescriptorCount);
                        for (var i = 0; i < originalTextureDescriptorCount; i++)
                        {
                            xivMtrl.TextureDescriptorList.Add(new TextureDescriptorStruct
                            {
                                TextureType = br.ReadUInt32(),
                                FileFormat = br.ReadInt16(),
                                Unknown = br.ReadInt16(),
                                TextureIndex = br.ReadUInt32()
                            });
                        }

                        var shaderBytes = br.ReadBytes(originalShaderParameterDataSize);

                        foreach (var shaderParam in xivMtrl.ShaderParameterList)
                        {
                            var offset = shaderParam.Offset;
                            var size = shaderParam.Size;
                            shaderParam.Bytes = new List<byte>();
                            for(var idx = offset; idx < offset + size; idx++)
                            {
                                shaderParam.Bytes.Add(shaderBytes[idx]);
                            }
                        }


                    }
                }));
            }
            finally
            {
                _semaphoreSlim.Release();
            }

            return xivMtrl;
        }

        /// <summary>
        /// Converts an xivMtrl to a XivTex for ColorSet exporting
        /// </summary>
        /// <param name="xivMtrl">The XivMtrl with the ColorSet data</param>
        /// <returns>The XivTex of the ColorSet</returns>
        public Task<XivTex> MtrlToXivTex(XivMtrl xivMtrl, TexTypePath ttp)
        {
            return Task.Run(() =>
            {
                var colorSetData = new List<byte>();

                foreach (var colorSetHalf in xivMtrl.ColorSetData)
                {
                    colorSetData.AddRange(BitConverter.GetBytes(colorSetHalf.RawValue));
                }

                var xivTex = new XivTex
                {
                    Width = 4,
                    Height = 16,
                    MipMapCount = 0,
                    TexData = colorSetData.ToArray(),
                    TextureFormat = XivTexFormat.A16B16G16R16F,
                    TextureTypeAndPath = ttp
                };

                return xivTex;
            });
        }

        /// <summary>
        /// Saves the Extra data from the ColorSet
        /// </summary>
        /// <param name="item">The item containing the ColorSet</param>
        /// <param name="xivMtrl">The XivMtrl for the ColorSet</param>
        /// <param name="saveDirectory">The save directory</param>
        /// <param name="race">The selected race for the item</param>
        public void SaveColorSetExtraData(IItem item, XivMtrl xivMtrl, DirectoryInfo saveDirectory, XivRace race)
        {
            if (xivMtrl.ColorSetExtraData != null)
            {
                var path = IOUtil.MakeItemSavePath(item, saveDirectory, race);

                Directory.CreateDirectory(path);

                var savePath = Path.Combine(path, Path.GetFileNameWithoutExtension(xivMtrl.MTRLPath) + ".dat");

                File.WriteAllBytes(savePath, xivMtrl.ColorSetExtraData);
            }
        }


        /// <summary>
        /// Imports a XivMtrl by converting it to bytes, then injecting it.
        /// </summary>
        /// <param name="xivMtrl">The XivMtrl containing the mtrl data</param>
        /// <param name="item">The item whos mtrl is being imported</param>
        /// <param name="source">The source/application that is writing to the dat.</param>
        /// <returns>The new offset</returns>
        public async Task<int> ImportMtrl(XivMtrl xivMtrl, IItem item, string source)
        {
            try
            {
                var mtrlBytes = CreateMtrlFile(xivMtrl, item);
                var dat = new Dat(_gameDirectory);


                // The MTRL file is now ready to go, but we need to validate the texture paths and create them if needed.
                var mapInfoList = xivMtrl.GetAllMapInfos(false);
                var _index = new Index(_gameDirectory);
                var _tex = new Tex(_gameDirectory);
                foreach (var mapInfo in mapInfoList)
                {
                    var path = mapInfo.path;
                    var fileHash = HashGenerator.GetHash(Path.GetFileName(path));
                    var pathHash = HashGenerator.GetHash(path.Substring(0, path.LastIndexOf("/", StringComparison.Ordinal)));
                    var exists = await _index.FileExists(fileHash, pathHash, XivDataFile._04_Chara);

                    if(exists)
                    {
                        continue;
                    }

                    var format = XivTexFormat.DXT1;
                    if(mapInfo.Usage == XivTexType.Normal)
                    {
                        if(mapInfo.Format == MtrlTextureDescriptorFormat.UsesColorset)
                        {
                            format = XivTexFormat.DXT5;
                        } else
                        {
                            format = XivTexFormat.A8R8G8B8;
                        }
                    }

                    var xivTex = new XivTex();
                    xivTex.TextureTypeAndPath = new TexTypePath()
                    {
                        DataFile = XivDataFile._04_Chara, Path = path, Type = mapInfo.Usage
                    };
                    xivTex.TextureFormat = format;

                    var di = Tex.GetDefaultTexturePath(xivTex.TextureFormat);

                    var newOffset = await _tex.TexDDSImporter(xivTex, item, di, "AddNewTextureNameToMaterial");

                }
               
                return await dat.ImportType2Data(mtrlBytes.ToArray(), item.Name, xivMtrl.MTRLPath, item.ItemCategory, source);
            }
            catch(Exception ex)
            {
                throw ex;
            }
        }

        /// <summary>
        /// Converts an XivMtrl object into the raw bytes of a Mtrl file.
        /// </summary>
        /// <param name="xivMtrl">The XivMtrl containing the mtrl data</param>
        /// <param name="item">The item</param>
        /// <returns>The new mtrl file byte data</returns>
        public byte[] CreateMtrlFile(XivMtrl xivMtrl, IItem item)
        {

            var mtrlBytes = new List<byte>();

            mtrlBytes.AddRange(BitConverter.GetBytes(xivMtrl.Signature));

            var fileSizePointer = mtrlBytes.Count;
            mtrlBytes.AddRange(BitConverter.GetBytes((ushort)0)); //Backfilled later
            mtrlBytes.AddRange(BitConverter.GetBytes(xivMtrl.ColorSetDataSize));

            var materialDataSizePointer = mtrlBytes.Count;
            mtrlBytes.AddRange(BitConverter.GetBytes((ushort)0)); //Backfilled later

            var texturePathsDataSizePointer = mtrlBytes.Count;
            mtrlBytes.AddRange(BitConverter.GetBytes((ushort)0)); //Backfilled later

            mtrlBytes.Add((byte)xivMtrl.TexturePathList.Count);
            mtrlBytes.Add((byte)xivMtrl.MapPathList.Count);
            mtrlBytes.Add((byte)xivMtrl.ColorSetPathList.Count);
            mtrlBytes.Add(xivMtrl.UnknownDataSize);

            // Regenerate offset list as we build the string list.
            xivMtrl.TexturePathOffsetList.Clear();
            xivMtrl.MapPathOffsetList.Clear();
            xivMtrl.ColorSetPathOffsetList.Clear();


            var stringListBytes = new List<byte>();

            foreach (var texPathString in xivMtrl.TexturePathList)
            {
                xivMtrl.TexturePathOffsetList.Add(stringListBytes.Count);
                stringListBytes.AddRange(Encoding.UTF8.GetBytes(texPathString.Replace("--", string.Empty)));
                stringListBytes.Add(0);
            }

            foreach (var mapPathString in xivMtrl.MapPathList)
            {
                xivMtrl.MapPathOffsetList.Add(stringListBytes.Count);
                stringListBytes.AddRange(Encoding.UTF8.GetBytes(mapPathString));
                stringListBytes.Add(0);
            }

            foreach (var colorSetPathString in xivMtrl.ColorSetPathList)
            {
                xivMtrl.ColorSetPathOffsetList.Add(stringListBytes.Count);
                stringListBytes.AddRange(Encoding.UTF8.GetBytes(colorSetPathString));
                stringListBytes.Add(0);
            }

            xivMtrl.TexturePathsDataSize = (ushort)stringListBytes.Count;

            stringListBytes.AddRange(Encoding.UTF8.GetBytes(xivMtrl.Shader));
            stringListBytes.Add(0);

            var padding = (stringListBytes.Count % 8);
            if (padding != 0)
            {
                padding = 8 - padding;
            }

            stringListBytes.AddRange(new byte[padding]);
            xivMtrl.MaterialDataSize = (ushort)stringListBytes.Count;




            // Write the new offset list.
            for (var i = 0; i < xivMtrl.TexturePathOffsetList.Count; i++)
            {
                mtrlBytes.AddRange(BitConverter.GetBytes((short)xivMtrl.TexturePathOffsetList[i]));
                mtrlBytes.AddRange(BitConverter.GetBytes((short)xivMtrl.TexturePathUnknownList[i]));
            }

            for (var i = 0; i < xivMtrl.MapPathOffsetList.Count; i++)
            {
                mtrlBytes.AddRange(BitConverter.GetBytes((short)xivMtrl.MapPathOffsetList[i]));
                mtrlBytes.AddRange(BitConverter.GetBytes((short)xivMtrl.MapPathUnknownList[i]));
            }

            for (var i = 0; i < xivMtrl.ColorSetPathOffsetList.Count; i++)
            {
                mtrlBytes.AddRange(BitConverter.GetBytes((short)xivMtrl.ColorSetPathOffsetList[i]));
                mtrlBytes.AddRange(BitConverter.GetBytes((short)xivMtrl.ColorSetPathUnknownList[i]));
            }

            // Write the actual string list (including padding).
            mtrlBytes.AddRange(stringListBytes);

            mtrlBytes.AddRange(xivMtrl.Unknown2);

            foreach (var colorSetHalf in xivMtrl.ColorSetData)
            {
                mtrlBytes.AddRange(BitConverter.GetBytes(colorSetHalf.RawValue));
            }

            if (xivMtrl.ColorSetDataSize == 544)
            {
                mtrlBytes.AddRange(xivMtrl.ColorSetExtraData);
            }


            mtrlBytes.AddRange(BitConverter.GetBytes(xivMtrl.ShaderParameterDataSize));
            mtrlBytes.AddRange(BitConverter.GetBytes(xivMtrl.TextureUsageCount));
            mtrlBytes.AddRange(BitConverter.GetBytes(xivMtrl.ShaderParameterCount));
            mtrlBytes.AddRange(BitConverter.GetBytes(xivMtrl.TextureDescriptorCount));
            mtrlBytes.AddRange(BitConverter.GetBytes(xivMtrl.ShaderNumber));

            mtrlBytes.AddRange(BitConverter.GetBytes(xivMtrl.Unknown3));

            foreach (var dataStruct1 in xivMtrl.TextureUsageList)
            {
                mtrlBytes.AddRange(BitConverter.GetBytes(dataStruct1.TextureType));
                mtrlBytes.AddRange(BitConverter.GetBytes(dataStruct1.Unknown));
            }

            foreach (var dataStruct2 in xivMtrl.ShaderParameterList)
            {
                mtrlBytes.AddRange(BitConverter.GetBytes((uint)dataStruct2.ParameterID));
                mtrlBytes.AddRange(BitConverter.GetBytes(dataStruct2.Offset));
                mtrlBytes.AddRange(BitConverter.GetBytes(dataStruct2.Size));
            }

            foreach (var parameterStruct in xivMtrl.TextureDescriptorList)
            {
                mtrlBytes.AddRange(BitConverter.GetBytes(parameterStruct.TextureType));
                mtrlBytes.AddRange(BitConverter.GetBytes(parameterStruct.FileFormat));
                mtrlBytes.AddRange(BitConverter.GetBytes(parameterStruct.Unknown));
                mtrlBytes.AddRange(BitConverter.GetBytes(parameterStruct.TextureIndex));
            }



            var ordered = (IEnumerable<ShaderParameterStruct>)xivMtrl.ShaderParameterList.OrderBy(x => x.Offset);
            var shaderBytes = new List<byte>();
            foreach (var shaderParam in ordered)
            {
                //shaderBytes.AddRange(Mtrl.ShaderParameterValues[shaderParam.ParameterID]);
                //shaderBytes.AddRange(new byte[shaderParam.Bytes.Count]);
                shaderBytes.AddRange(shaderParam.Bytes);
            }

            // Pad out if we're missing anything.
            if (shaderBytes.Count < xivMtrl.ShaderParameterDataSize)
            {
                shaderBytes.AddRange(new byte[xivMtrl.ShaderParameterDataSize - shaderBytes.Count]);
            }
            mtrlBytes.AddRange(shaderBytes);

            //mtrlBytes.AddRange(xivMtrl.AdditionalData);


            // Backfill the actual file size data.
            xivMtrl.FileSize = (short)mtrlBytes.Count;
            IOUtil.ReplaceBytesAt(mtrlBytes, BitConverter.GetBytes(xivMtrl.FileSize), fileSizePointer);

            xivMtrl.MaterialDataSize = (ushort)stringListBytes.Count;
            IOUtil.ReplaceBytesAt(mtrlBytes, BitConverter.GetBytes(xivMtrl.MaterialDataSize), materialDataSizePointer);

            IOUtil.ReplaceBytesAt(mtrlBytes, BitConverter.GetBytes(xivMtrl.TexturePathsDataSize), texturePathsDataSizePointer);
            return mtrlBytes.ToArray();
        }

        /// <summary>
        /// Gets the names of the textures based on file name
        /// </summary>
        /// <remarks>
        /// The name of the texture is obtained from the file name ending
        /// </remarks>
        /// <param name="texPathList">The list of texture paths</param>
        /// <returns>A list of TexTypePath</returns>
        private async Task<List<TexTypePath>> GetTexNames(IEnumerable<string> texPathList, XivDataFile dataFile)
        {
            var index = new Index(_gameDirectory);
            var texTypePathList = new List<TexTypePath>();

            foreach (var path in texPathList)
            {
                if (!await index.FileExists(HashGenerator.GetHash(Path.GetFileName(path)),
                    HashGenerator.GetHash(Path.GetDirectoryName(path).Replace("\\", "/")), dataFile))
                {
                    continue;
                }

                var ttp = new TexTypePath { Path = path, DataFile = dataFile };

                if (path.Contains("dummy") || path.Equals(string.Empty)) continue;

                if (path.Contains("_s.tex"))
                {
                    ttp.Type = XivTexType.Specular;
                }
                else if (path.Contains("_d.tex"))
                {
                    ttp.Type = XivTexType.Diffuse;

                }
                else if (path.Contains("_n.tex"))
                {
                    ttp.Type = XivTexType.Normal;

                }
                else if (path.Contains("_m.tex"))
                {
                    ttp.Type = path.Contains("skin") ? XivTexType.Skin : XivTexType.Multi;
                }

                texTypePathList.Add(ttp);
            }

            return texTypePathList;
        }

        /// <summary>
        /// Gets the mtrl path for a given item
        /// </summary>
        /// <param name="itemModel">Item that contains model data</param>
        /// <param name="xivRace">The race for the requested data</param>
        /// <param name="part">The mtrl part <see cref="GearInfo.GetPartList(IItemModel, XivRace)"/></param>
        /// <param name="itemType">The type of the item</param>
        /// <param name="type">The item type whether Primary or Secondary</param>
        /// <returns>A tuple containing the mtrl folder and file, and whether it has a vfx</returns>
        private async Task<(string Folder, string File, bool HasVfx)> GetMtrlPath(IItemModel itemModel, XivRace xivRace, char part, XivItemType itemType, string type)
        {
            // The default version number
            var version = "0001";

            var hasVfx = false;

            if (itemType != XivItemType.human && itemType != XivItemType.furniture && !type.Equals("Secondary"))
            {
                // get the items version from the imc file
                var imc = new Imc(_gameDirectory, DataFile);
                var imcInfo = await imc.GetImcInfo(itemModel, itemModel.ModelInfo);
                version = imcInfo.Version.ToString().PadLeft(4, '0');

                if (imcInfo.Vfx > 0)
                {
                    hasVfx = true;
                }
            }

            var id = itemModel.ModelInfo.ModelID.ToString().PadLeft(4, '0');
            var bodyVer = itemModel.ModelInfo.Body.ToString().PadLeft(4, '0');
            var itemCategory = itemModel.ItemCategory;

            if (type.Equals("Secondary"))
            {
                var xivGear = itemModel as XivGear;

                id = xivGear.SecondaryModelInfo.ModelID.ToString().PadLeft(4, '0');
                bodyVer = xivGear.SecondaryModelInfo.Body.ToString().PadLeft(4, '0');

                var imc = new Imc(_gameDirectory, DataFile);
                var imcInfo = await imc.GetImcInfo(itemModel, xivGear.SecondaryModelInfo);
                version = imcInfo.Version.ToString().PadLeft(4, '0');

                if (imc.ChangedType)
                {
                    itemCategory = XivStrings.Hands;
                    itemType = XivItemType.equipment;
                    xivRace = XivRace.Hyur_Midlander_Male;
                }
            }

            if (itemModel.Category.Equals(XivStrings.Character) && (itemCategory.Equals(XivStrings.Body) || itemCategory.Equals(XivStrings.Tail)))
            {
                version = type.PadLeft(4, '0');
            }

            var race = xivRace.GetRaceCode();

            string mtrlFolder = "", mtrlFile = "";

            switch (itemType)
            {
                case XivItemType.equipment:
                    mtrlFolder = $"chara/{itemType}/e{id}/material/v{version}";
                    mtrlFile = $"mt_c{race}e{id}_{SlotAbbreviationDictionary[itemCategory]}_{part}{MtrlExtension}";
                    break;
                case XivItemType.accessory:
                    mtrlFolder = $"chara/{itemType}/a{id}/material/v{version}";
                    mtrlFile = $"mt_c{race}a{id}_{SlotAbbreviationDictionary[itemCategory]}_{part}{MtrlExtension}";
                    break;
                case XivItemType.weapon:
                    mtrlFolder = $"chara/{itemType}/w{id}/obj/body/b{bodyVer}/material/v{version}";
                    mtrlFile = $"mt_w{id}b{bodyVer}_{part}{MtrlExtension}";
                    break;

                case XivItemType.monster:
                    mtrlFolder = $"chara/{itemType}/m{id}/obj/body/b{bodyVer}/material/v{version}";
                    mtrlFile = $"mt_m{id}b{bodyVer}_{part}{MtrlExtension}";
                    break;
                case XivItemType.demihuman:
                    mtrlFolder = $"chara/{itemType}/d{id}/obj/equipment/e{bodyVer}/material/v{version}";
                    mtrlFile = $"mt_d{id}e{bodyVer}_{SlotAbbreviationDictionary[itemModel.ItemSubCategory]}_{part}{MtrlExtension}";
                    break;
                case XivItemType.human:
                    if (itemCategory.Equals(XivStrings.Body))
                    {
                        if (_language != XivLanguage.Korean )
                        {
                            mtrlFolder = $"chara/{itemType}/c{race}/obj/body/b{bodyVer}/material/v{version}";
                        }
                        else
                        {
                            mtrlFolder = $"chara/{itemType}/c{race}/obj/body/b{bodyVer}/material";
                        }
                        mtrlFile = $"mt_c{race}b{bodyVer}_{part}{MtrlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Hair))
                    {
                        // Hair has a version number, but no IMC, so we leave it at the default 0001
                        mtrlFolder = $"chara/{itemType}/c{race}/obj/hair/h{bodyVer}/material/v{version}";
                        mtrlFile = $"mt_c{race}h{bodyVer}_{SlotAbbreviationDictionary[itemModel.ItemSubCategory]}_{part}{MtrlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Face))
                    {
                        mtrlFolder = $"chara/{itemType}/c{race}/obj/face/f{bodyVer}/material";
                        mtrlFile = $"mt_c{race}f{bodyVer}_{SlotAbbreviationDictionary[itemModel.ItemSubCategory]}_{part}{MtrlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Tail))
                    {
                        if (_language != XivLanguage.Korean )
                        {
                            mtrlFolder = $"chara/{itemType}/c{race}/obj/tail/t{bodyVer}/material/v{version}";
                        }
                        else
                        {
                            mtrlFolder = $"chara/{itemType}/c{race}/obj/tail/t{bodyVer}/material";
                        }
                        mtrlFile = $"mt_c{race}t{bodyVer}_{part}{MtrlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Ears))
                    {
                        mtrlFolder = $"chara/{itemType}/c{race}/obj/zear/z{bodyVer}/material";
                        mtrlFile = $"mt_c{race}z{bodyVer}_{SlotAbbreviationDictionary[itemModel.ItemSubCategory]}{part}{MtrlExtension}";
                    }
                    break;
                case XivItemType.furniture:
                    if (itemCategory.Equals(XivStrings.Furniture_Indoor))
                    {
                        mtrlFolder = $"bgcommon/hou/indoor/general/{id}/material";
                        mtrlFile = $"fun_b0_m{id}_0{part}{MtrlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Furniture_Outdoor))
                    {
                        mtrlFolder = $"bgcommon/hou/outdoor/general/{id}/material";
                        mtrlFile = $"gar_b0_m{id}_0{part}{MtrlExtension}";
                    }
                    break;
                default:
                    mtrlFolder = "";
                    mtrlFile = "";
                    break;
            }

            return (mtrlFolder, mtrlFile, hasVfx);
        }

        /// <summary>
        /// Gets the mtrl folder for a given item
        /// </summary>
        /// <param name="itemModel">Item that contains model data</param>
        /// <param name="xivRace">The race for the requested data</param>
        /// <param name="itemType">The type of the item</param>
        /// <returns>The mtrl Folder</returns>
        private async Task<string> GetMtrlFolder(IItemModel itemModel, XivRace xivRace, XivItemType itemType)
        {
            // The default version number
            var version = "0001";

            if (itemType != XivItemType.human && itemType != XivItemType.furniture)
            {
                // get the items version from the imc file
                var imc = new Imc(_gameDirectory, DataFile);
                var imcInfo = await imc.GetImcInfo(itemModel, itemModel.ModelInfo);
                version = imcInfo.Version.ToString().PadLeft(4, '0');
            }

            if (version.Equals("0000"))
            {
                version = "0001";
            }

            var id = itemModel.ModelInfo.ModelID.ToString().PadLeft(4, '0');
            var bodyVer = itemModel.ModelInfo.Body.ToString().PadLeft(4, '0');

            var race = xivRace.GetRaceCode();

            var mtrlFolder = "";

            switch (itemType)
            {
                case XivItemType.equipment:
                    mtrlFolder = $"chara/{itemType}/e{id}/material/v{version}";
                    break;
                case XivItemType.accessory:
                    mtrlFolder = $"chara/{itemType}/a{id}/material/v{version}";
                    break;
                case XivItemType.weapon:
                    mtrlFolder = $"chara/{itemType}/w{id}/obj/body/b{bodyVer}/material/v{version}";
                    break;
                case XivItemType.monster:
                    mtrlFolder = $"chara/{itemType}/m{id}/obj/body/b{bodyVer}/material/v{version}";
                    break;
                case XivItemType.demihuman:
                    mtrlFolder = $"chara/{itemType}/d{id}/obj/equipment/e{bodyVer}/material/v{version}";
                    break;
                case XivItemType.human:
                    if (itemModel.ItemCategory.Equals(XivStrings.Body))
                    {
                        if (_language != XivLanguage.Korean )
                        {
                            mtrlFolder = $"chara/{itemType}/c{race}/obj/body/b{bodyVer}/material/v{version}";
                        }
                        else
                        {
                            mtrlFolder = $"chara/{itemType}/c{race}/obj/body/b{bodyVer}/material";
                        }
                    }
                    else if (itemModel.ItemCategory.Equals(XivStrings.Hair))
                    {
                        // Hair has a version number, but no IMC, so we leave it at the default 0001
                        mtrlFolder = $"chara/{itemType}/c{race}/obj/hair/h{bodyVer}/material/v{version}";
                    }
                    else if (itemModel.ItemCategory.Equals(XivStrings.Face))
                    {
                        mtrlFolder = $"chara/{itemType}/c{race}/obj/face/f{bodyVer}/material";
                    }
                    else if (itemModel.ItemCategory.Equals(XivStrings.Tail))
                    {
                        if (_language != XivLanguage.Korean )
                        {
                            mtrlFolder = $"chara/{itemType}/c{race}/obj/tail/t{bodyVer}/material/v{version}";
                        }
                        else
                        {
                            mtrlFolder = $"chara/{itemType}/c{race}/obj/tail/t{bodyVer}/material";
                        }
                    }
                    else if (itemModel.ItemCategory.Equals(XivStrings.Ears))
                    {
                        mtrlFolder = $"chara/{itemType}/c{race}/obj/zear/z{bodyVer}/material";
                    }
                    break;
                case XivItemType.furniture:
                    if (itemModel.ItemCategory.Equals(XivStrings.Furniture_Indoor))
                    {
                        mtrlFolder = $"bgcommon/hou/indoor/general/{id}/material";
                    }
                    else if (itemModel.ItemCategory.Equals(XivStrings.Furniture_Outdoor))
                    {
                        mtrlFolder = $"bgcommon/hou/outdoor/general/{id}/material";
                    }
                    break;
                default:
                    mtrlFolder = "";
                    break;
            }

            return mtrlFolder;
        }

        public void Dipose()
        {
            _semaphoreSlim?.Dispose();
        }

        /// <summary>
        /// A dictionary containing the slot abbreviations in the format [equipment slot, slot abbreviation]
        /// </summary>
        private static readonly Dictionary<string, string> SlotAbbreviationDictionary = new Dictionary<string, string>
        {
            {XivStrings.Head, "met"},
            {XivStrings.Hands, "glv"},
            {XivStrings.Legs, "dwn"},
            {XivStrings.Feet, "sho"},
            {XivStrings.Body, "top"},
            {XivStrings.Ears, "ear"},
            {XivStrings.Neck, "nek"},
            {XivStrings.Rings, "rir"},
            {XivStrings.Wrists, "wrs"},
            {XivStrings.Head_Body, "top"},
            {XivStrings.Body_Hands, "top"},
            {XivStrings.Body_Hands_Legs, "top"},
            {XivStrings.Body_Legs_Feet, "top"},
            {XivStrings.Body_Hands_Legs_Feet, "top"},
            {XivStrings.Legs_Feet, "top"},
            {XivStrings.All, "top"},
            {XivStrings.Face, "fac"},
            {XivStrings.Iris, "iri"},
            {XivStrings.Etc, "etc"},
            {XivStrings.Accessory, "acc"},
            {XivStrings.Hair, "hir"},
            {XivStrings.InnerEar, "fac_"},
            {XivStrings.OuterEar, ""}
        };

        /// <summary>
        /// A dictionary containing race data in the format [Race ID, XivRace]
        /// </summary>
        private static readonly Dictionary<string, XivRace> IDRaceDictionary = new Dictionary<string, XivRace>
        {
            {"0101", XivRace.Hyur_Midlander_Male},
            {"0104", XivRace.Hyur_Midlander_Male_NPC},
            {"0201", XivRace.Hyur_Midlander_Female},
            {"0204", XivRace.Hyur_Midlander_Female_NPC},
            {"0301", XivRace.Hyur_Highlander_Male},
            {"0304", XivRace.Hyur_Highlander_Male_NPC},
            {"0401", XivRace.Hyur_Highlander_Female},
            {"0404", XivRace.Hyur_Highlander_Female_NPC},
            {"0501", XivRace.Elezen_Male},
            {"0504", XivRace.Elezen_Male_NPC},
            {"0601", XivRace.Elezen_Female},
            {"0604", XivRace.Elezen_Female_NPC},
            {"0701", XivRace.Miqote_Male},
            {"0704", XivRace.Miqote_Male_NPC},
            {"0801", XivRace.Miqote_Female},
            {"0804", XivRace.Miqote_Female_NPC},
            {"0901", XivRace.Roegadyn_Male},
            {"0904", XivRace.Roegadyn_Male_NPC},
            {"1001", XivRace.Roegadyn_Female},
            {"1004", XivRace.Roegadyn_Female_NPC},
            {"1101", XivRace.Lalafell_Male},
            {"1104", XivRace.Lalafell_Male_NPC},
            {"1201", XivRace.Lalafell_Female},
            {"1204", XivRace.Lalafell_Female_NPC},
            {"1301", XivRace.AuRa_Male},
            {"1304", XivRace.AuRa_Male_NPC},
            {"1401", XivRace.AuRa_Female},
            {"1404", XivRace.AuRa_Female_NPC},
            {"1501", XivRace.Hrothgar},
            {"1504", XivRace.Hrothgar_NPC},
            {"1801", XivRace.Viera},
            {"1804", XivRace.Viera_NPC},
            {"9104", XivRace.NPC_Male},
            {"9204", XivRace.NPC_Female}
        };
    }
}