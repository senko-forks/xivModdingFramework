﻿using HelixToolkit.SharpDX.Core.Helper;
using Newtonsoft.Json.Linq;
using SharpDX;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using xivModdingFramework.Cache;
using xivModdingFramework.Exd.Enums;
using xivModdingFramework.Exd.FileTypes;
using xivModdingFramework.Items.DataContainers;
using xivModdingFramework.Materials.DataContainers;
using xivModdingFramework.Mods;
using xivModdingFramework.Mods.DataContainers;
using xivModdingFramework.SqPack.DataContainers;
using xivModdingFramework.SqPack.FileTypes;
using static xivModdingFramework.Materials.FileTypes.STM;

namespace xivModdingFramework.Materials.FileTypes
{
    /// <summary>
    /// Staining Template File Handler
    /// </summary>
    public static class STM
    {
        public enum EStainingTemplate
        {
            Endwalker,
            Dawntrail
        };

        public static Dictionary<EStainingTemplate, string> STMFilePaths = new Dictionary<EStainingTemplate, string>()
        {
            { EStainingTemplate.Endwalker, "chara/base_material/stainingtemplate.stm" },
            { EStainingTemplate.Dawntrail, "chara/base_material/stainingtemplate_gud.stm" },
        };


        public static ushort GetTemplateKeyFromMaterialData(XivMtrl mtrl, int row)
        {
            return GetTemplateKeyFromMaterialData(mtrl.ColorSetDyeData, row);
        }
        public static ushort GetTemplateKeyFromMaterialData(byte[] data, int row)
        {
            if(data == null)
            {
                return 0;
            }

            if(data.Length == 128)
            {
                // Dawntrail Style
                var value = BitConverter.ToUInt32(data, row * 4);
                return GetTemplateKeyFromMaterialData(value);

            } else if(data.Length == 32)
            {
                // Endwalker Style
                var value = BitConverter.ToUInt16(data, row * 2);
                return GetTemplateKeyFromMaterialData(value);
            }
            return 0;
        }

        /// <summary>
        /// Retrieve template ID from Dawntrail Dye Data
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static ushort GetTemplateKeyFromMaterialData(uint data)
        {
            return (ushort)(data << 5 >> 21);
        }

        /// <summary>
        /// Retrieve template ID from Endwalker Dye Data
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static ushort GetTemplateKeyFromMaterialData(ushort data)
        {
            return (ushort)(data >> 5);
        }

        public static async Task<StainingTemplateFile> GetStainingTemplateFile(EStainingTemplate template, bool forceOriginal = false, IndexFile index = null, ModList modlist = null)
        {
            var _dat = new Dat(XivCache.GameInfo.GameDirectory);

            var path = STMFilePaths[template];

            var data = await _dat.GetType2Data(path, forceOriginal, index, modlist);

            var ret = new StainingTemplateFile(data, template);
            return ret;
        }

        public static async Task SaveStainingTemplateFile(StainingTemplateFile file, string applicationSource, IndexFile index = null, ModList modlist = null)
        {
            throw new NotImplementedException();

        }
        public static async Task<Dictionary<int, string>> GetDyeNames()
        {

            var lang = XivCache.GameInfo.GameLanguage;
            if (lang == General.Enums.XivLanguage.None)
            {
                lang = General.Enums.XivLanguage.English;
            }

            Dictionary<int, string> Dyes = new Dictionary<int, string>();

            var ex = new Ex(XivCache.GameInfo.GameDirectory, lang);
            var exData = await ex.ReadExData(XivEx.stain);


            var dataLength = exData[0].Length - 2;

            foreach (var kv in exData)
            {
                if (kv.Key == 0) continue;

                var size = kv.Value.Length - dataLength;
                var name = Encoding.UTF8.GetString(kv.Value, dataLength, size).Replace("\0", "");
                var dyeId = kv.Key - 1;
                if (String.IsNullOrEmpty(name)) {
                    name = "Dye " + dyeId.ToString();
                }

                Dyes.Add(dyeId, name);
            }


            return Dyes;
        }
    }

    public enum StainingTemplateArrayType
    {
        Singleton,
        OneToOne,
        Indexed
    }

    public class StainingTemplateEntry
    {
        // Data Entries, in the format of
        // [Data Offset] => [Dye Id] = Dye values
        public readonly List<List<Half[]>> Entries = new List<List<Half[]>>();

        public Half[] GetDiffuseData(int dyeId = 0)
        {
            return GetData(0, dyeId);
        }
        public Half[] GetSpecularData(int dyeId = 0)
        {
            return GetData(1, dyeId);
        }
        public Half[] GetEmissiveData(int dyeId = 0)
        {
            return GetData(2, dyeId);
        }
        public Half[] GetSpecularPowerData(int dyeId = 0)
        {
            return GetData(3, dyeId);
        }
        public Half[] GetGlossData(int dyeId = 0)
        {
            return GetData(4, dyeId);
        }

        public Half[] GetData(int offset, int dyeId = 0)
        {
            if(offset >= Entries.Count)
            {
                return null;
            }

            if (Entries[offset].Count > dyeId)
            {
                return Entries[offset][dyeId];
            }
            return null;
        }

        public static Dictionary<EStainingTemplate, Dictionary<int, int>> TemplateEntryOffsetToColorsetOffset = new Dictionary<EStainingTemplate, Dictionary<int, int>>()
        {
            { EStainingTemplate.Endwalker, new Dictionary<int, int>() {
                { 0, 0 },
                { 1, 4 },
                { 2, 8 },
                { 3, 3 },
                { 4, 7 },
            }},
            { EStainingTemplate.Dawntrail, new Dictionary<int, int>() {
                { 0, 0 },
                { 1, 4 },
                { 2, 8 },
                { 3, 11 },
                { 4, 18 },
                { 5, 16 },
                { 6, 12 },
                { 7, 13 },
                { 8, 14 },
                { 9, 19 },
                { 10, 27 },
                { 11, 21 }
            }},
        };

        public StainingTemplateEntry(byte[] data, int offset, EStainingTemplate templateType)
        {
            var arrayEnds = new List<ushort>();
            var start = offset;

            var _ItemCount = 12;
            if(templateType == EStainingTemplate.Endwalker)
            {
                _ItemCount = 5;
            }


            // This format sucks.
            for (int i = 0; i < _ItemCount; i++)
            {
                arrayEnds.Add(BitConverter.ToUInt16(data, offset));
                offset += 2;
            }
            int headerSize = _ItemCount * 2;


            var lastOffset = 0;
            for (int x = 0; x < _ItemCount; x++)
            {
                Entries.Add(new List<Half[]>());
                var elementSize = 1;
                if(x < 3)
                {
                    // First 3 entries are triple entries.
                    elementSize = 3;
                }

                var arraySize = (arrayEnds[x] - lastOffset) / elementSize;
                var type = StainingTemplateArrayType.OneToOne;

                // Calculate the data type.
                var indexStart = 0;
                if (arraySize == 1)
                {
                    // Single entry used for everything.
                    type = StainingTemplateArrayType.Singleton;
                }
                if(arraySize == 0)
                {
                    // No data.
                    continue;
                }
                else if (arraySize < 128)
                {
                    // Indexed array, where we have [n] # of real entries,
                    // then 128 one-byte index entries referencing those [n] entries.
                    var totalBytes = (arrayEnds[x] - lastOffset) *2;
                    var remBytes = totalBytes - 128;

                    indexStart = start + headerSize + (lastOffset * 2) + remBytes;

                    arraySize = remBytes / 2 / elementSize;
                    type = StainingTemplateArrayType.Indexed;
                }

                var arrayStart = lastOffset;
                var offsetStart = (start + headerSize + (arrayStart * 2));

                List<Half[]> halfData  = new List<Half[]>();

                for (int i = 0; i < arraySize; i++)
                {

                    Half[] halfs = new Half[elementSize];

                    var elementStart = offsetStart + ((i * 2) * elementSize);

                    halfs[0] = new Half(BitConverter.ToUInt16(data, elementStart));
                    if (elementSize > 1)
                    {
                        halfs[1] = new Half(BitConverter.ToUInt16(data, elementStart + 2));
                        halfs[2] = new Half(BitConverter.ToUInt16(data, elementStart + 4));
                    }

                    halfData.Add(halfs);
                }

                if(type == StainingTemplateArrayType.Indexed)
                {
                    var nArray = new List<Half[]>();
                    var indexes = new byte[128];
                    for (int i = 0; i < 128; i++)
                    {
                        try
                        {
                            var index = data[indexStart + i + 1];
                            var entry = new Half[3];
                            if (index > halfData.Count)
                            {
                                nArray.Add(new Half[] { new Half(), new Half(), new Half() });
                                continue;
                            }

                            if (index == 0)
                            {
                                nArray.Add(new Half[] { new Half(), new Half(), new Half() });
                                continue;
                            }

                            index -= 1;

                            nArray.Add(halfData[index]);
                        } catch(Exception ex)
                        {
                            throw;
                        }
                    }

                    halfData = nArray;
                }

                if (halfData.Count == 1)
                {
                    for (int i = 0; i < 127; i++)
                    {
                        halfData.Add(halfData[0]);
                    }
                }


                foreach (var arr in halfData)
                {
                    Entries[x].Add(arr);
                }

                lastOffset = arrayEnds[x];
            }

            var length = lastOffset;
        }

    }

    public class StainingTemplateFile
    {
        private uint Header;
        private Dictionary<ushort, StainingTemplateEntry> Templates = new Dictionary<ushort, StainingTemplateEntry>();

        public readonly EStainingTemplate TemplateType;
        public List<ushort> GetKeys()
        {
            return Templates.Keys.ToList();
        }

        public void SetTemplate(ushort key, StainingTemplateEntry entry)
        {
            if (Templates.ContainsKey(key))
            {
                Templates[key] = entry;
            }
            else
            {
                Templates.Add(key, entry);
            }
        }
        public StainingTemplateEntry GetTemplate(ushort key)
        {
            if (Templates.ContainsKey(key))
            {
                return Templates[key];
            }
            return null;
        }
        public StainingTemplateFile(byte[] data, EStainingTemplate templateType)
        {
            TemplateType = templateType;
            // Get header size and # of entries.
            var Header = BitConverter.ToUInt16(data, 0);
            var entryCount = BitConverter.ToUInt16(data, 4);
            var unknown = BitConverter.ToUInt16(data, 6);


            Dictionary<ushort, ushort> entryOffsets = new Dictionary<ushort, ushort>();

            List<ushort> keys = new List<ushort>();
            List<ushort> values = new List<ushort>();
            List<int> sizes = new List<int>();
            var offset = 8;

            // Read template Ids
            for (int i = 0; i < entryCount; i++)
            {
                var key = BitConverter.ToUInt16(data, offset);
                entryOffsets.Add(key, 0);
                keys.Add(key);
                offset += 2;
            }

            const int _headerEntrySize = 4;
            var endOfHeader = (8 + (_headerEntrySize * entryCount));

            for (int i = 0; i < entryCount; i++)
            {
                entryOffsets[keys[i]] = (ushort) ((BitConverter.ToUInt16(data, offset) * 2) + endOfHeader);
                offset += 2;
            }


            var idx = 0;
            foreach (var kv in entryOffsets)
            {
                var entry = new StainingTemplateEntry(data, kv.Value, templateType);
                Templates.Add(kv.Key, entry);
                idx++;
            }
        }



    }
}
