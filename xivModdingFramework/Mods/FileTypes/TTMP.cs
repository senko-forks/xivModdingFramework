// xivModdingFramework
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

using Ionic.Zip;
using Newtonsoft.Json;
using SharpDX;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Security.Permissions;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using xivModdingFramework.Cache;
using xivModdingFramework.Exd.FileTypes;
using xivModdingFramework.General;
using xivModdingFramework.General.Enums;
using xivModdingFramework.Helpers;
using xivModdingFramework.Materials.FileTypes;
using xivModdingFramework.Models.FileTypes;
using xivModdingFramework.Mods.DataContainers;
using xivModdingFramework.Resources;
using xivModdingFramework.SqPack.DataContainers;
using xivModdingFramework.SqPack.FileTypes;

using Index = xivModdingFramework.SqPack.FileTypes.Index;

namespace xivModdingFramework.Mods.FileTypes
{
    public class TTMP
    {
        // These file types are forbidden from being included in Modpacks or being imported via modpacks.
        // This is because these file types are re-built from constituent smaller files, and thus importing
        // a complete file would bash the user's current file state in unpredictable ways.
        public static readonly HashSet<string> ForbiddenModTypes = new HashSet<string>()
        {
            ".cmp", ".imc", ".eqdp", ".eqp", ".gmp", ".est"
        };

        private readonly string _currentWizardTTMPVersion = "2.0w";
        private readonly string _currentSimpleTTMPVersion = "2.0s";
        private readonly string _currentBackupTTMPVersion = "2.0b";
        private const string _minimumAssembly = "1.3.0.0";

        private string _tempMPD, _tempMPL, _source;
        private readonly DirectoryInfo _modPackDirectory;

        public TTMP(DirectoryInfo modPackDirectory, string source)
        {
            _modPackDirectory = modPackDirectory;
            _source = source;
        }

        /// <summary>
        /// Creates a mod pack that uses a wizard for installation
        /// </summary>
        /// <param name="modPackData">The data that will go into the mod pack</param>
        /// <param name="progress">The progress of the mod pack creation</param>
        /// <returns>The number of pages created for the mod pack</returns>
        public async Task<int> CreateWizardModPack(ModPackData modPackData, IProgress<double> progress, bool overwriteModpack)
        {
            var processCount = await Task.Run<int>(() =>
            {
                var guid = Guid.NewGuid();

                var dir = Path.Combine(Path.GetTempPath(), guid.ToString());
                Directory.CreateDirectory(dir);

                _tempMPD = Path.Combine(dir, "TTMPD.mpd");
                _tempMPL = Path.Combine(dir, "TTMPL.mpl");

                var imageList = new HashSet<string>();
                var pageCount = 1;

                Version version = modPackData.Version == null ? new Version(1, 0, 0, 0) : modPackData.Version;
                var modPackJson = new ModPackJson
                {
                    TTMPVersion = _currentWizardTTMPVersion,
                    MinimumFrameworkVersion = _minimumAssembly,
                    Name = modPackData.Name,
                    Author = modPackData.Author,
                    Version = version.ToString(),
                    Description = modPackData.Description,
                    Url = modPackData.Url,
                    ModPackPages = new List<ModPackPageJson>()
                };

                using (var binaryWriter = new BinaryWriter(File.Open(_tempMPD, FileMode.Create)))
                {
                    foreach (var modPackPage in modPackData.ModPackPages)
                    {
                        var modPackPageJson = new ModPackPageJson
                        {
                            PageIndex = modPackPage.PageIndex,
                            ModGroups = new List<ModGroupJson>()
                        };

                        modPackJson.ModPackPages.Add(modPackPageJson);

                        foreach (var modGroup in modPackPage.ModGroups)
                        {
                            var modGroupJson = new ModGroupJson
                            {
                                GroupName = modGroup.GroupName,
                                SelectionType = modGroup.SelectionType,
                                OptionList = new List<ModOptionJson>()
                            };

                            modPackPageJson.ModGroups.Add(modGroupJson);

                            foreach (var modOption in modGroup.OptionList)
                            {
                                var imageFileName = "";
                                if (modOption.Image != null)
                                {
                                    var fname = Path.GetFileName(modOption.ImageFileName);
                                    imageFileName = Path.Combine(dir, fname);
                                    File.Copy(modOption.ImageFileName, imageFileName, true);
                                    imageList.Add(imageFileName);
                                }

                                var fn = imageFileName == "" ? "" : "images/" + Path.GetFileName(imageFileName);
                                var modOptionJson = new ModOptionJson
                                {
                                    Name = modOption.Name,
                                    Description = modOption.Description,
                                    ImagePath = fn,
                                    GroupName = modOption.GroupName,
                                    SelectionType = modOption.SelectionType,
                                    IsChecked=modOption.IsChecked,
                                    ModsJsons = new List<ModsJson>()
                                };

                                modGroupJson.OptionList.Add(modOptionJson);

                                foreach (var modOptionMod in modOption.Mods)
                                {
                                    var dataFile = GetDataFileFromPath(modOptionMod.Key);

                                    if (ForbiddenModTypes.Contains(Path.GetExtension(modOptionMod.Key))) continue;
                                    var modsJson = new ModsJson
                                    {
                                        Name = modOptionMod.Value.Name,
                                        Category = modOptionMod.Value.Category.GetEnDisplayName(),
                                        FullPath = modOptionMod.Key,
                                        IsDefault = modOptionMod.Value.IsDefault,
                                        ModSize = modOptionMod.Value.ModDataBytes.Length,
                                        ModOffset = binaryWriter.BaseStream.Position,
                                        DatFile = dataFile.GetDataFileName(),
                                    };

                                    binaryWriter.Write(modOptionMod.Value.ModDataBytes);

                                    modOptionJson.ModsJsons.Add(modsJson);
                                }
                            }
                        }

                        progress?.Report((double)pageCount / modPackData.ModPackPages.Count);

                        pageCount++;
                    }
                }

                File.WriteAllText(_tempMPL, JsonConvert.SerializeObject(modPackJson));

                var modPackPath = Path.Combine(_modPackDirectory.FullName, $"{modPackData.Name}.ttmp2");

                if (File.Exists(modPackPath) && !overwriteModpack)
                {
                    var fileNum = 1;
                    modPackPath = Path.Combine(_modPackDirectory.FullName, $"{modPackData.Name}({fileNum}).ttmp2");
                    while (File.Exists(modPackPath))
                    {
                        fileNum++;
                        modPackPath = Path.Combine(_modPackDirectory.FullName, $"{modPackData.Name}({fileNum}).ttmp2");
                    }
                }
                else if (File.Exists(modPackPath) && overwriteModpack)
                {
                    File.Delete(modPackPath);
                }

                var zf = new ZipFile();
                zf.UseZip64WhenSaving = Zip64Option.AsNecessary;
                zf.CompressionLevel = Ionic.Zlib.CompressionLevel.None;
                zf.AddFile(_tempMPL, "");
                zf.AddFile(_tempMPD, "");
                zf.Save(modPackPath);

                foreach (var image in imageList)
                {
                    zf.AddFile(image, "images");
                }
                zf.Save(modPackPath);


                File.Delete(_tempMPD);
                File.Delete(_tempMPL);

                return pageCount;
            });

            return processCount;
        }

        /// <summary>
        /// Creates a mod pack that uses simple installation
        /// </summary>
        /// <param name="modPackData">The data that will go into the mod pack</param>
        /// <param name="gameDirectory">The game directory</param>
        /// <param name="progress">The progress of the mod pack creation</param>
        /// <returns>The number of mods processed for the mod pack</returns>
        public async Task<int> CreateSimpleModPack(SimpleModPackData modPackData, DirectoryInfo gameDirectory, IProgress<(int current, int total, string message)> progress, bool overwriteModpack)
        {
            var processCount = await Task.Run<int>(() =>
            {
                var dat = new Dat(gameDirectory);

                var guid = Guid.NewGuid();

                var dir = Path.Combine(Path.GetTempPath(), guid.ToString());
                Directory.CreateDirectory(dir);


                _tempMPD = Path.Combine(dir, "TTMPD.mpd");
                _tempMPL = Path.Combine(dir, "TTMPL.mpl");

                var modCount = 0;

                var modPackJson = new ModPackJson
                {
                    TTMPVersion = _currentSimpleTTMPVersion,
                    Name = modPackData.Name,
                    Author = modPackData.Author,
                    Version = modPackData.Version.ToString(),
                    MinimumFrameworkVersion = _minimumAssembly,
                    Url = modPackData.Url,
                    Description = modPackData.Description,
                    SimpleModsList = new List<ModsJson>()
                };

                try
                {
                    using (var binaryWriter = new BinaryWriter(File.Open(_tempMPD, FileMode.Create)))
                    {
                        foreach (var simpleModData in modPackData.SimpleModDataList)
                        {
                            if (ForbiddenModTypes.Contains(Path.GetExtension(simpleModData.FullPath))) continue;

                            var modsJson = new ModsJson
                            {
                                Name = simpleModData.Name,
                                Category = simpleModData.Category.GetEnDisplayName(),
                                FullPath = simpleModData.FullPath,
                                ModSize = simpleModData.ModSize,
                                DatFile = simpleModData.DatFile,
                                IsDefault = simpleModData.IsDefault,
                                ModOffset = binaryWriter.BaseStream.Position,
                                ModPackEntry = new ModPack
                                {
                                    name =  modPackData.Name,
                                    author = modPackData.Author,
                                    version = modPackData.Version.ToString(),
                                    url = modPackData.Url
                                }
                            };

                            var rawData = dat.GetRawData(simpleModData.ModOffset,
                                XivDataFiles.GetXivDataFile(simpleModData.DatFile),
                                simpleModData.ModSize);

                            if (rawData == null)
                            {
                                throw new Exception("Unable to obtain data for the following mod\n\n" +
                                                    $"Name: {simpleModData.Name}\nFull Path: {simpleModData.FullPath}\n" +
                                                    $"Mod Offset: {simpleModData.ModOffset}\nData File: {simpleModData.DatFile}\n\n" +
                                                    $"Unselect the above mod and try again.");
                            }

                            binaryWriter.Write(rawData);

                            modPackJson.SimpleModsList.Add(modsJson);

                            progress?.Report((++modCount, modPackData.SimpleModDataList.Count, string.Empty));
                        }
                    }

                    progress?.Report((0, modPackData.SimpleModDataList.Count, GeneralStrings.TTMP_Creating));

                    File.WriteAllText(_tempMPL, JsonConvert.SerializeObject(modPackJson));

                    var modPackPath = Path.Combine(_modPackDirectory.FullName, $"{modPackData.Name}.ttmp2");

                    if (File.Exists(modPackPath) && !overwriteModpack)
                    {
                        var fileNum = 1;
                        modPackPath = Path.Combine(_modPackDirectory.FullName, $"{modPackData.Name}({fileNum}).ttmp2");
                        while (File.Exists(modPackPath))
                        {
                            fileNum++;
                            modPackPath = Path.Combine(_modPackDirectory.FullName, $"{modPackData.Name}({fileNum}).ttmp2");
                        }
                    }
                    else if (File.Exists(modPackPath) && overwriteModpack)
                    {
                        File.Delete(modPackPath);
                    }

                    var zf = new ZipFile();
                    zf.UseZip64WhenSaving = Zip64Option.AsNecessary;
                    zf.CompressionLevel = Ionic.Zlib.CompressionLevel.None;
                    zf.AddFile(_tempMPL, "");
                    zf.AddFile(_tempMPD, "");
                    zf.Save(modPackPath);
                }
                finally
                {
                    Directory.Delete(dir, true);
                }

                return modCount;
            });

            return processCount;
        }

        /// <summary>
        /// Creates a backup modpack which retains the original modpacks on import
        /// </summary>
        /// <param name="backupModpackData">The data that will go into the mod pack</param>
        /// <param name="gameDirectory">The game directory</param>
        /// <param name="progress">The progress of the mod pack creation</param>
        /// <param name="overwriteModpack">Whether or not to overwrite an existing modpack with the same name</param>
        /// <returns>The number of mods processed for the mod pack</returns>
        public async Task<int> CreateBackupModpack(BackupModPackData backupModpackData, DirectoryInfo gameDirectory, IProgress<(int current, int total, string message)> progress, bool overwriteModpack)
        {
            var processCount = await Task.Run<int>(() =>
            {
                var dat = new Dat(gameDirectory);

                var guid = Guid.NewGuid();

                var dir = Path.Combine(Path.GetTempPath(), guid.ToString());
                Directory.CreateDirectory(dir);


                _tempMPD = Path.Combine(dir, "TTMPD.mpd");
                _tempMPL = Path.Combine(dir, "TTMPL.mpl");

                var modCount = 0;

                var modPackJson = new ModPackJson
                {
                    TTMPVersion = _currentBackupTTMPVersion,
                    Name = backupModpackData.Name,
                    Author = backupModpackData.Author,
                    Version = backupModpackData.Version.ToString(),
                    MinimumFrameworkVersion = _minimumAssembly,
                    Url = backupModpackData.Url,
                    Description = backupModpackData.Description,
                    SimpleModsList = new List<ModsJson>()
                };

                try
                {
                    using (var binaryWriter = new BinaryWriter(File.Open(_tempMPD, FileMode.Create)))
                    {
                        foreach (var backupModData in backupModpackData.ModsToBackup)
                        {
                            if (ForbiddenModTypes.Contains(Path.GetExtension(backupModData.SimpleModData.FullPath))) continue;

                            var modsJson = new ModsJson
                            {
                                Name = backupModData.SimpleModData.Name,
                                Category = backupModData.SimpleModData.Category.GetEnDisplayName(),
                                FullPath = backupModData.SimpleModData.FullPath,
                                ModSize = backupModData.SimpleModData.ModSize,
                                DatFile = backupModData.SimpleModData.DatFile,
                                IsDefault = backupModData.SimpleModData.IsDefault,
                                ModOffset = binaryWriter.BaseStream.Position,
                                ModPackEntry = backupModData.ModPack,
                            };

                            var rawData = dat.GetRawData(backupModData.SimpleModData.ModOffset,
                                XivDataFiles.GetXivDataFile(backupModData.SimpleModData.DatFile),
                                backupModData.SimpleModData.ModSize);

                            if (rawData == null)
                            {
                                throw new Exception("Unable to obtain data for the following mod\n\n" +
                                                    $"Name: {backupModData.SimpleModData.Name}\nFull Path: {backupModData.SimpleModData.FullPath}\n" +
                                                    $"Mod Offset: {backupModData.SimpleModData.ModOffset}\nData File: {backupModData.SimpleModData.DatFile}\n\n" +
                                                    $"Unselect the above mod and try again.");
                            }

                            binaryWriter.Write(rawData);

                            modPackJson.SimpleModsList.Add(modsJson);

                            progress?.Report((++modCount, backupModpackData.ModsToBackup.Count, string.Empty));
                        }
                    }

                    progress?.Report((0, backupModpackData.ModsToBackup.Count, GeneralStrings.TTMP_Creating));

                    File.WriteAllText(_tempMPL, JsonConvert.SerializeObject(modPackJson));

                    var modPackPath = Path.Combine(_modPackDirectory.FullName, $"{backupModpackData.Name}.ttmp2");

                    if (File.Exists(modPackPath) && !overwriteModpack)
                    {
                        var fileNum = 1;
                        modPackPath = Path.Combine(_modPackDirectory.FullName, $"{backupModpackData.Name}({fileNum}).ttmp2");
                        while (File.Exists(modPackPath))
                        {
                            fileNum++;
                            modPackPath = Path.Combine(_modPackDirectory.FullName, $"{backupModpackData.Name}({fileNum}).ttmp2");
                        }
                    }
                    else if (File.Exists(modPackPath) && overwriteModpack)
                    {
                        File.Delete(modPackPath);
                    }

                    var zf = new ZipFile();
                    zf.UseZip64WhenSaving = Zip64Option.AsNecessary;
                    zf.CompressionLevel = Ionic.Zlib.CompressionLevel.None;
                    zf.AddFile(_tempMPL, "");
                    zf.AddFile(_tempMPD, "");
                    zf.Save(modPackPath);
                }
                finally
                {
                    Directory.Delete(dir, true);
                }

                return modCount;
            });

            return processCount;
        }

        /// <summary>
        /// Gets the data from a mod pack including images if present
        /// </summary>
        /// <param name="modPackDirectory">The directory of the mod pack</param>
        /// <returns>A tuple containing the mod pack json data and a dictionary of images if any</returns>
        public static Task<(ModPackJson ModPackJson, Dictionary<string, Image> ImageDictionary)> GetModPackJsonData(DirectoryInfo modPackDirectory)
        {
            return Task.Run(() =>
            {
                ModPackJson modPackJson = null;
                var imageDictionary = new Dictionary<string, Image>();

                using (var zf = ZipFile.Read(modPackDirectory.FullName))
                {
                    var images = zf.Entries.Where(x => x.FileName.EndsWith(".png") || x.FileName.StartsWith("images/"));
                    var mpl = zf.Entries.First(x => x.FileName.EndsWith(".mpl"));

                    using (var streamReader = new StreamReader(mpl.OpenReader()))
                    {
                        var jsonString = streamReader.ReadToEnd();

                        modPackJson = JsonConvert.DeserializeObject<ModPackJson>(jsonString);
                    }

                    foreach(var imgEntry in images)
                    {
                        imageDictionary.Add(imgEntry.FileName, Image.Load(imgEntry.OpenReader()));
                    }
                }

                return (modPackJson, imageDictionary);
            });
        }

        public static Task<byte[]> GetModPackData(DirectoryInfo modPackDirectory)
        {
            return Task.Run(() =>
            {
                using (var zf = ZipFile.Read(modPackDirectory.FullName))
                {
                    var mpd = zf.Entries.First(x => x.FileName.EndsWith(".mpd"));

                    using (var ms = new MemoryStream())
                    {
                        mpd.Extract(ms);
                        return ms.ToArray();
                    }
                }
                //using (var stream = new ZipInputStream(modPackDirectory.FullName))
                //{
                //    while (stream.GetNextEntry() is var entry)
                //    {
                //        if (entry.FileName.EndsWith(".mpd"))
                //        {
                //            stream.Read(buffer, 0, modJson.ModSize);
                //            stream.Read(buffer, 0, modJson.ModSize);
                //            stream.Read(buffer, 0, modJson.ModSize);
                //            break;
                //        }
                //    }
                //};
            });
        }

        /// <summary>
        /// Gets the data from first generation mod packs
        /// </summary>
        /// <param name="modPackDirectory">The directory of the mod pack</param>
        /// <returns>A list containing original mod pack json data</returns>
        public Task<List<OriginalModPackJson>> GetOriginalModPackJsonData(DirectoryInfo modPackDirectory)
        {
            return Task.Run(() =>
            {
                var modPackJsonList = new List<OriginalModPackJson>();

                using (var archive = System.IO.Compression.ZipFile.OpenRead(modPackDirectory.FullName))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith(".mpl"))
                        {
                            using (var streamReader = new StreamReader(entry.Open()))
                            {
                                var line = streamReader.ReadLine();
                                if (line.ToLower().Contains("version"))
                                {
                                    // Skip this line and read the next
                                    line = streamReader.ReadLine();
                                    if (line == null) return null;
                                    modPackJsonList.Add(JsonConvert.DeserializeObject<OriginalModPackJson>(line));
                                }
                                else
                                {
                                    modPackJsonList.Add(JsonConvert.DeserializeObject<OriginalModPackJson>(line));
                                }

                                while (streamReader.Peek() >= 0)
                                {
                                    line = streamReader.ReadLine();
                                    modPackJsonList.Add(JsonConvert.DeserializeObject<OriginalModPackJson>(line));
                                }
                            }
                        }
                    }
                }

                return modPackJsonList;
            });
        }

        /// <summary>
        /// Gets the version from a mod pack
        /// </summary>
        /// <param name="packPath">Path to the overlay pack file.</param>
        /// <param name="modifierFunction">Intermediate function that performs any user requested modifications to the overlay pack.  Returns true if the import should be aborted.</param>
        /// <param name="source">Standard import source name</param>
        /// <returns>The version of the mod pack as a string</returns>
        public static string GetVersion(DirectoryInfo modPackDirectory)
        {
            ModPackJson modPackJson = null;

            using (var archive = System.IO.Compression.ZipFile.OpenRead(modPackDirectory.FullName))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".mpl"))
                    {
                        using (var streamReader = new StreamReader(entry.Open()))
                        {
                            var jsonString = streamReader.ReadToEnd();
                            try
                            {
                                modPackJson = JsonConvert.DeserializeObject<ModPackJson>(jsonString);
                            } catch(Exception ex)
                            {
                                return "1.0";
                            }
                        }
                    }
                }
            }

            return modPackJson.TTMPVersion;
        }

        /// <summary>
        /// Imports an overlay pack asynchronously.
        /// </summary>
        /// <param name="packPath">Path to the .ttop file.</param>
        /// <param name="modifierFunction">Intermediate function, function takes the overlay pack to modify, and a string indicating the active temporary zip directory.  Returns true if the process should abort</param>
        /// <returns></returns>
        public async Task<(int ImportCount, int ErrorCount, string Errors, float Duration)> ImportOverlaypackAsync(string packPath, Func<OverlayPack, string, Task<bool>> modifierFunction = null)
        {
            if (XivCache.GameInfo.UseLumina)
            {
                throw new Exception("Cannot import modpacks via TexTools in Lumina mode.");
            }

            // We don't need to stop the cache worker for overlay imports, since Textures are the bottom of the dependency chain.

            var _modding = new Modding(XivCache.GameInfo.GameDirectory);
            var modList = _modding.GetModList();

            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            try
            {
                Directory.CreateDirectory(tempPath);

                // 0 - Extract the header file.
                using (var zf = ZipFile.Read(packPath))
                {
                    zf.ExtractAll(tempPath);
                }

                var rootFile = "list.json";

                var listPath = Path.Combine(tempPath, rootFile);

                var overlayPack = JsonConvert.DeserializeObject<OverlayPack>(File.ReadAllText(listPath));

                // Throw the pack to the modifier function if there is one.
                if (modifierFunction != null)
                {
                    var abort = await modifierFunction(overlayPack, tempPath);
                    if (abort)
                    {
                        return new(0, 0, "User Cancelled Import Process.", 0);
                    }
                }

                var mp = new ModPack();
                mp.name = overlayPack.Name;
                mp.author = overlayPack.Author;
                mp.url = overlayPack.URL;
                mp.version = overlayPack.ModVersion;

                modList.ModPacks.Add(mp);

                var _mtrl = new Mtrl(XivCache.GameInfo.GameDirectory);
                var _index = new Index(XivCache.GameInfo.GameDirectory);

                var index = await _index.GetIndexFile(XivDataFile._04_Chara);

                var imported = 0;
                var filesPath = Path.Combine(tempPath, "files");
                foreach(var kv in overlayPack.Files)
                {
                    var overlay = kv.Value; 
                    var filePath = Path.Combine(filesPath, overlay.ZipPath);
                    var fileStream = File.Open(filePath, FileMode.Open);
                    var moddedTex = await _mtrl.ApplyOverlayToMaterial(overlay.MtrlPath, overlay.TexType, fileStream, _source, index, modList);
                    
                    // Bind this new mod entry to our overlay modpack.
                    var modEntry = modList.Mods.First(x => x.fullPath == moddedTex);
                    modEntry.modPack = mp;

                    imported++;
                }


                // Save changes.
                await _index.SaveIndexFile(index);
                await _modding.SaveModListAsync(modList);


                return new(imported, 0, null, 0);
            }
            finally
            {
                try
                {
                    // Clean up temp files.
                    Directory.Delete(tempPath, true);
                } catch
                {

                }
            }

        }

        /// <summary>
        /// Imports a mod pack asynchronously 
        /// </summary>
        /// <param name="modPackDirectory">The directory of the mod pack</param>
        /// <param name="modsJson">The list of mods to be imported</param>
        /// <param name="gameDirectory">The game directory</param>
        /// <param name="modListDirectory">The mod list directory</param>
        /// <param name="progress">The progress of the import</param>
        /// <param name="GetRootConversionsFunction">Function called part-way through import to resolve rood conversions, if any are desired.  Function takes a List of files, the in-progress modified index and modlist files, and returns a dictionary of conversion data.  If this function throws and OperationCancelledException, the import is cancelled.</param>
        /// <param name="AutoAssignBodyMaterials">Whether models should be scanned for auto material assignment or not.</param>
        /// <returns>The number of total mods imported</returns>
        public async Task<(int ImportCount, int ErrorCount, string Errors, float Duration)> ImportModPackAsync(
            DirectoryInfo modPackDirectory, List<ModsJson> modsJson, DirectoryInfo gameDirectory, DirectoryInfo modListDirectory, IProgress<(int current, int total, string message)> progress, 
            Func<HashSet<string>, Dictionary<XivDataFile, IndexFile>, ModList, Task<Dictionary<XivDependencyRoot, (XivDependencyRoot Root, int Variant)>>>  GetRootConversionsFunction = null,
            bool AutoAssignBodyMaterials = false, ModPackJson ModPackData = null, bool fixPreDawntrailMods = true, bool updateShaders = false)
        {
            if (modsJson == null || modsJson.Count == 0) return (0, 0, "", 0);

            if(XivCache.GameInfo.UseLumina)
            {
                throw new Exception("Cannot import modpacks via TexTools in Lumina mode.");
            }

            var startTime = DateTime.Now.Ticks;
            long endTime = 0;
            long part1Duration = 0;
            long part2Duration = 0;

            var dat = new Dat(gameDirectory);
            var modding = new Modding(gameDirectory);
            var importErrors = "";

            // Disable the cache woker while we're installing multiple items at once, so that we don't process queue items mid-import.
            // (Could result in improper parent file calculations, as the parent files may not be actually imported yet)
            var workerEnabled = XivCache.CacheWorkerEnabled;
            XivCache.CacheWorkerEnabled = false;


            // Loop through all the incoming mod entries, and only take
            // the *LAST* mod json entry for each file path.
            // This keeps us from having to constantly re-query the mod list file, and filters out redundant imports.
            var filePaths = new HashSet<string>();
            var filteredModsJson = new List<ModsJson>(modsJson.Count);
            for(int i = modsJson.Count -1; i >= 0; i--)
            {
                var mj = modsJson[i];
                if(filePaths.Contains(mj.FullPath))
                {
                    // Already have a mod using this path, discard this mod entry.
                    continue;
                }

                // Don't allow importing forbidden mod types.
                if (ForbiddenModTypes.Contains(Path.GetExtension(mj.FullPath))) continue;

                filePaths.Add(mj.FullPath);
                filteredModsJson.Add(mj);
            }

            if(filteredModsJson.Count == 0)
            {
                return (0, 0, "", 0);
            }

            var totalFiles = filePaths.Count;

            HashSet<string> ErroneousFiles = new HashSet<string>();

            try
            {
                await Task.Run(async () =>
                {

                    // Okay, we need to do a few things here.
                    // 0 - Extract the MPD file (tests so far with streaming the ZIP data have all failed)
                    // 1 - Copy all the mod data to the DAT files.
                    // 2 - Update all the indices.
                    // 3 - Update the Modlist
                    // 4 - Expand Metadata
                    // 5 - Queue Cache Updates.

                    // We only need the actual Zip file during the initial copy stage.

                    Dictionary<string, uint> DatOffsets = new Dictionary<string, uint>();
                    Dictionary<XivDataFile, List<string>> FilesPerDf = new Dictionary<XivDataFile, List<string>>();
                    Dictionary<string, int> FileTypes = new Dictionary<string, int>();


                    var _modding = new Modding(XivCache.GameInfo.GameDirectory);
                    var modList = _modding.GetModList();
                    var needsTexFix = DoesTexNeedFixing(modPackDirectory);

                    // 0 - Extract the MPD file.
                    using (var zf = ZipFile.Read(modPackDirectory.FullName))
                    {
                        progress.Report((0, 0, "Unzipping TTMP File..."));
                        var mpd = zf.Entries.First(x => x.FileName.EndsWith(".mpd"));
                        var mpl = zf.Entries.First(x => x.FileName.EndsWith(".mpl"));

                        _tempMPD = Path.GetTempFileName();
                        _tempMPL = Path.GetTempFileName();

                        using (var fs = new FileStream(_tempMPL, FileMode.Open))
                        {
                            mpl.Extract(fs);
                        }

                        using (var fs = new FileStream(_tempMPD, FileMode.Open))
                        {
                            mpd.Extract(fs);
                        }
                    }

                    Dictionary<string, Mod> modsByFile = new Dictionary<string, Mod>();

                    foreach (var mod in modList.Mods)
                    {
                        if (!modsByFile.ContainsKey(mod.fullPath))
                        {
                            modsByFile.Add(mod.fullPath, mod);
                        }
                    }

                    // 1 - Copy all the mod data to the DAT files.
                    var count = 0;
                    progress.Report((0, 0, "Writing new mod data to DAT files..."));
                    using (var binaryReader = new BinaryReader(new FileStream(_tempMPD, FileMode.Open)))
                    {
                        foreach (var modJson in filteredModsJson)
                        {
                            try
                            {
                                binaryReader.BaseStream.Seek(modJson.ModOffset, SeekOrigin.Begin);
                                var data = binaryReader.ReadBytes(modJson.ModSize);

                                if (modJson.FullPath.EndsWith(".tex") && needsTexFix)
	                                FixupTextoolsTex(data);

                                var df = IOUtil.GetDataFileFromPath(modJson.FullPath);

                                var size = data.Length;
                                if (size % 256 != 0)
                                {
                                    size += (256 - (size % 256));
                                }

                                Mod mod = null;
                                if (modsByFile.ContainsKey(modJson.FullPath))
                                {
                                    mod = modsByFile[modJson.FullPath];
                                }

                                // Always write data to end of file during modpack imports in case we need
                                // to roll back the import.
                                uint offset = await dat.WriteToDat(data, df);
                                DatOffsets.Add(modJson.FullPath, offset);

                                var dataType = BitConverter.ToInt32(data, 4);
                                FileTypes.Add(modJson.FullPath, dataType);

                                if (!FilesPerDf.ContainsKey(df))
                                {
                                    FilesPerDf.Add(df, new List<string>());
                                }

                                FilesPerDf[df].Add(modJson.FullPath);
                            }
                            catch (Exception ex)
                            {
                                ErroneousFiles.Add(modJson.FullPath);
                                importErrors +=
                                    $"Name: {Path.GetFileName(modJson.FullPath)}\nPath: {modJson.FullPath}\nImport Stage: Data Writing\nError: {ex.Message}\n\n";
                            }

                            count++;
                            progress.Report((count, totalFiles, "Writing new mod data to DAT files..."));
                        }
                    }


                    File.Delete(_tempMPL);
                    File.Delete(_tempMPD);

                    count = 0;
                    progress.Report((count, totalFiles, "Updating Index file references..."));

                    // We've now copied the data into the game files, we now need to update the indices.
                    var _index = new Index(XivCache.GameInfo.GameDirectory);
                    Dictionary<string, uint> OriginalOffsets = new Dictionary<string, uint>();
                    Dictionary<XivDataFile, IndexFile> modifiedIndexFiles = new Dictionary<XivDataFile, IndexFile>();
                    Dictionary<XivDataFile, IndexFile> originalIndexFiles = new Dictionary<XivDataFile, IndexFile>();
                    foreach (var kv in FilesPerDf)
                    {
                        // Load each index file and update all the files within it as needed.
                        var df = kv.Key;
                        modifiedIndexFiles.Add(df, await _index.GetIndexFile(df));
                        originalIndexFiles.Add(df, await _index.GetIndexFile(df, false, true));
                        var index = modifiedIndexFiles[df];

                        foreach (var file in kv.Value)
                        {
                            if (ErroneousFiles.Contains(file)) continue;

                            try
                            {
                                var original = index.SetDataOffset(file, DatOffsets[file]);
                                OriginalOffsets.Add(file, original);
                            } catch(Exception ex)
                            {
                                ErroneousFiles.Add(file);
                                importErrors +=
                                    $"Name: {Path.GetFileName(file)}\nPath: {file}\nImport Stage: Index Writing\nError: {ex.Message}\n\n";
                            }
                        }


                        count++;
                        progress.Report((count, totalFiles, "Updating Index file references..."));
                    }

                    // Add entries for new modpacks to the mod list 
                    foreach (var modsJson in filteredModsJson)
                    {
                        if (modsJson.ModPackEntry == null) continue;

                        var modPackExists = modList.ModPacks.Any(modpack => modpack.name == modsJson.ModPackEntry.name);

                        if (!modPackExists)
                        {
                            modList.ModPacks.Add(modsJson.ModPackEntry);
                        }
                    }


                    if (GetRootConversionsFunction != null)
                    {
                        // Get the modpack to list the conversions under, this is the just the modpack entry of the first modsJson since they're all the same unless it's a backup
                        // However, this code shouldn't be used when importing backup modpacks since they already had the choice to change the destination item after the initial import
                        var modPack = modList.ModPacks.First(x => x.name == filteredModsJson[0].ModPackEntry?.name);

                        Dictionary<XivDependencyRoot, (XivDependencyRoot Root, int Variant)> rootConversions = null;
                        try
                        {
                            progress.Report((count, totalFiles, "Waiting on Destination Item Selection..."));

                            endTime = DateTime.Now.Ticks;

                            // Duration in ms
                            part1Duration = (endTime - startTime) / 10000;


                            rootConversions = await GetRootConversionsFunction(filePaths, modifiedIndexFiles, modList);
                        } catch(OperationCanceledException ex)
                        {
                            // User cancelled the function or otherwise a critical error happened in the conversion function.
                            // Cancell the import without saving anything.
                            ErroneousFiles.Add("n/a");
                            totalFiles = 0;
                            importErrors = "User Cancelled Import Process.";
                            progress.Report((0, 0, "User Cancelled Import Process."));
                            return;
                        }

                        startTime = DateTime.Now.Ticks;

                        if (rootConversions != null && rootConversions.Count > 0)
                        {
                            progress.Report((0, 0, "Updating Destination Items..."));
                            // If we have roots to convert, we get to do some extra work here.

                            // We currently have all the files loaded into our in-memory indices in their default locations.
                            var conversionsByDf = rootConversions.GroupBy(x => IOUtil.GetDataFileFromPath(x.Key.Info.GetRootFile()));

                            HashSet<string> filesToReset = new HashSet<string>();
                            foreach (var dfe in conversionsByDf)
                            {
                                var df = dfe.Key;
                                foreach (var conversion in dfe)
                                {
                                    var source = conversion.Key;
                                    var destination = conversion.Value.Root;
                                    var variant = conversion.Value.Variant;

                                    var convertedFiles = await RootCloner.CloneRoot(source, destination, _source, variant, null, null, modifiedIndexFiles[df], modList, modPack);

                                    // We're going to reset the original files back to their pre-modpack install state after, as long as they got moved.
                                    foreach (var fileKv in convertedFiles)
                                    {
                                        // Remove the file from our json list, the conversion already handled everything we needed to do with it.
                                        var json = filteredModsJson.RemoveAll(x => x.FullPath == fileKv.Key);

                                        if (fileKv.Key != fileKv.Value)
                                        {
                                            filesToReset.Add(fileKv.Key);
                                        }

                                        filePaths.Remove(fileKv.Key);

                                        var mod = modList.Mods.FirstOrDefault(x => x.fullPath == fileKv.Value);
                                        if (mod != null)
                                        {
                                            mod.modPack = modPack;
                                        }
                                    }

                                    // Remove any remaining lingering files which belong to this root.
                                    // Ex. Extraneous unused files in the modpack.  This helps keep
                                    // any unnecessary files from poluting the original destination item post conversion.
                                    foreach(var file in filePaths.ToList())
                                    {
                                        var root = XivDependencyGraph.ExtractRootInfo(file);
                                        if(root == source.Info)
                                        {
                                            filePaths.Remove(file);
                                            filesToReset.Add(file);
                                        }
                                    }
                                }

                                // Reset the index pointers back to previous.
                                foreach (var file in filesToReset)
                                {
                                    var oldOffset = originalIndexFiles[df].Get8xDataOffset(file);
                                    modifiedIndexFiles[df].SetDataOffset(file, oldOffset);
                                }
                            }
                        }
                    }

                    // Save the modified files.
                    foreach(var dkv in modifiedIndexFiles)
                    {
                        await _index.SaveIndexFile(dkv.Value);
                    }

                    // Dat files and indices are updated, time to update the modlist.

                    count = 0;
                    progress.Report((count, totalFiles, "Updating Mod List file..."));

                    // Update the Mod List file.

                    foreach (var file in filePaths)
                    {
                        if (ErroneousFiles.Contains(file)) continue;
                        try
                        {
                            var json = filteredModsJson.FirstOrDefault(x => x.FullPath == file);
                            if (json == null) continue;


                            var mod = modList.Mods.FirstOrDefault(x => x.fullPath == file);
                            var longOffset = ((long)DatOffsets[file]) * 8L;
                            var originalOffset = OriginalOffsets[file];
                            var longOriginal = ((long)originalOffset) * 8L;
                            var fileType = FileTypes[file];
                            var df = IOUtil.GetDataFileFromPath(file);


                            var size = json.ModSize;
                            if (size % 256 != 0)
                            {
                                size += 256 - (size % 256);
                            }

                            if (mod == null)
                            {
                                // Determine if this is an original game file or not.
                                var fileAdditionMod = originalOffset == 0;

                                mod = new Mod()
                                {
                                    name = json.Name,
                                    category = json.Category,
                                    datFile = df.GetDataFileName(),
                                    source = _source,
                                    fullPath = file,
                                    data = new Data()
                                };

                                mod.data.modSize = size;
                                mod.data.modOffset = longOffset;
                                mod.data.originalOffset = (fileAdditionMod ? longOffset : longOriginal);
                                mod.data.dataType = fileType;
                                mod.enabled = true;
                                mod.modPack = json.ModPackEntry;
                                modList.Mods.Add(mod);

                            }
                            else
                            {
                                var fileAdditionMod = originalOffset == 0 || mod.IsCustomFile();
                                if (fileAdditionMod)
                                {
                                    mod.data.originalOffset = longOffset;
                                }

                                mod.data.modSize = size;
                                mod.data.modOffset = longOffset;
                                mod.enabled = true;
                                mod.modPack = json.ModPackEntry;
                                mod.data.dataType = fileType;
                                mod.name = json.Name;
                                mod.category = json.Category;
                                mod.source = _source;
                            }
                        }
                        catch (Exception ex)
                        {
                            ErroneousFiles.Add(file);
                            importErrors +=
                                $"Name: {Path.GetFileName(file)}\nPath: {file}\nImport Stage: Modlist Update\nError: {ex.Message}\n\n";
                        }

                        count++;
                        progress.Report((count, totalFiles, "Updating Mod List file..."));
                    }
                    await _modding.SaveModListAsync(modList);


                    var totalMetadataEntries = filePaths.Count(x => x.EndsWith(".meta"));
                    count = 0;
                    progress.Report((count, totalMetadataEntries, "Expanding Metadata Files..."));

                    // ModList is updated now.  Time to expand the Metadata files.
                    Dictionary<XivDataFile, IndexFile> indexFiles = new Dictionary<XivDataFile, IndexFile>();
                    Dictionary<XivDataFile, List<ItemMetadata>> metadataEntries = new Dictionary<XivDataFile, List<ItemMetadata>>();
                    foreach (var file in filePaths)
                    {
                        if (ErroneousFiles.Contains(file)) continue;

                        var longOffset = ((long)DatOffsets[file]) * 8L;
                        var ext = Path.GetExtension(file);
                        if (ext == ".meta")
                        {
                            try
                            {
                                var df = IOUtil.GetDataFileFromPath(file);
                                if (!indexFiles.ContainsKey(df))
                                {
                                    indexFiles.Add(df, await _index.GetIndexFile(df));
                                    metadataEntries.Add(df, new List<ItemMetadata>());
                                }

                                var metaRaw = await dat.GetType2Data(longOffset, df);
                                var meta = await ItemMetadata.Deserialize(metaRaw);

                                meta.Validate(file);

                                metadataEntries[df].Add(meta);
                            } catch(Exception ex)
                            {
                                ErroneousFiles.Add(file);
                                importErrors +=
                                    $"Name: {Path.GetFileName(file)}\nPath: {file}\nImport Stage: Metadata Expansion\nError: {ex.Message}\n\n";
                            }
                            count++;
                            progress.Report((count, totalMetadataEntries, "Expanding Metadata Files..."));
                        } else if(ext == ".rgsp")
                        {
                            if (!indexFiles.ContainsKey(XivDataFile._04_Chara))
                            {
                                indexFiles.Add(XivDataFile._04_Chara, await _index.GetIndexFile(XivDataFile._04_Chara));
                                metadataEntries.Add(XivDataFile._04_Chara, new List<ItemMetadata>());
                            }
                            // Expand the racial scaling files
                            await CMP.ApplyRgspFile(file, indexFiles[XivDataFile._04_Chara], modList);
                        }
                    }

                    try
                    {
                        foreach (var ifKv in indexFiles)
                        {
                            if (metadataEntries.ContainsKey(ifKv.Key))
                            {
                                await ItemMetadata.ApplyMetadataBatched(metadataEntries[ifKv.Key], ifKv.Value, modList);
                            }

                        }
                    } catch(Exception Ex)
                    {
                        throw new Exception("An error occured when attempting to expand the metadata entries.\n\nError:" + Ex.Message);
                    }

                    foreach(var kv in indexFiles)
                    {
                        await _index.SaveIndexFile(kv.Value);
                    }
                    await _modding.SaveModListAsync(modList);


                    if (AutoAssignBodyMaterials) {
                        progress.Report((0, 0, "Scanning for body material corrections..."));

                        // Find all relevant models..
                        var modelFiles = filteredModsJson.Where(x => x.FullPath.EndsWith(".mdl"));
                        var usableModels = modelFiles.Where(x => Mdl.IsAutoAssignableModel(x.FullPath)).ToList();

                        if(usableModels.Any())
                        {
                            var indexFile = await _index.GetIndexFile(XivDataFile._04_Chara);
                            var modelCount = usableModels.Count;
                            progress.Report((0, modelCount, "Scanning and updating body models..."));
                            var _mdl = new Models.FileTypes.Mdl(XivCache.GameInfo.GameDirectory);

                            // Loop them to perform heuristic check.
                            var anyChanges = false;
                            var i = 0;
                            foreach (var mdlEntry in usableModels)
                            {
                                i++;
                                var file = mdlEntry.FullPath;
                                progress.Report((i, modelCount, "Scanning and updating body models..."));
                                var changed = await _mdl.CheckSkinAssignment(mdlEntry.FullPath, indexFile, modList);
                                anyChanges |= changed;
                            }

                            // Save the modified index/modlist.
                            if(anyChanges)
                            {
                                await _index.SaveIndexFile(indexFile);
                                await _modding.SaveModListAsync(modList);
                            }
                        }
                    }

                    // If we have a Pre Dawntrail Modpack, we need to fix things up.
                    if (fixPreDawntrailMods)
                    {
                        if (ModPackData != null && Int32.Parse(ModPackData.TTMPVersion.Substring(0, 1)) <= 1)
                        {
                            await FixPreDawntrailImports(filePaths, updateShaders, "DawntrailFix", progress);
                        }
                    }

                    count = 0;
                    progress.Report((0, 0, "Queuing Cache Updates..."));
                    // Metadata files expanded, last thing is to queue everthing up for the Cache.
                    var files = filteredModsJson.Select(x => x.FullPath).ToList();
                    try
                    {
                        XivCache.QueueDependencyUpdate(files);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception("An error occured while trying to update the Cache.\n\n" + ex.Message + "\n\nThe mods were still imported successfully, however, the Cache should be rebuilt.");
                    }
                });

                progress.Report((totalFiles, totalFiles, "Job Done."));
            } finally
            {
                XivCache.CacheWorkerEnabled = workerEnabled;
            }

            var errorCount = ErroneousFiles.Count;
            var count = totalFiles - errorCount;

            endTime = DateTime.Now.Ticks;

            // Duration in ms
            part2Duration = (endTime - startTime) / 10000;

            float seconds = (part1Duration + part2Duration) / 1000f;

            return (count, errorCount, importErrors, seconds);
        }

        /// <summary>
        /// Parse the version out of this modpack to determine whether or not we need
        /// to add 80 to the uncompressed size of the Tex files contained within.
        /// </summary>
        /// <param name="mpd">The path to the modpack.</param>
        /// <returns>True if we must modify tex header uncompressed sizes, false otherwise.</returns>
        private static bool DoesTexNeedFixing(DirectoryInfo mpd) {

	        var ver = GetVersion(mpd);
	        if (string.IsNullOrEmpty(ver))
		        return true;

	        var newVer = ver;

	        var lastChar = ver.Substring(ver.Length - 1)[0];
	        if (char.IsLetter(lastChar))
		        newVer = ver.Substring(0, ver.Length - 1);

	        double.TryParse(newVer, out var verDouble);

	        return verDouble < 1.3;
        }

        /// <summary>
        /// Fix xivModdingFramework TEX quirks.
        /// </summary>
        /// <param name="tex">The TEX data to be fixed up.</param>
        public static void FixupTextoolsTex(byte[] tex) {

	        // Read the uncompressed size from the file
	        var size = BitConverter.ToInt32(tex, 8);
	        var newSize = size + 80;
	        
	        byte[] buffer = BitConverter.GetBytes(newSize);
	        tex[8] = buffer[0];
	        tex[9] = buffer[1];
	        tex[10] = buffer[2];
	        tex[11] = buffer[3];
        }

        /// <summary>
        /// Gets the data type from an item path
        /// </summary>
        /// <param name="path">The path of the item</param>
        /// <returns>The data type</returns>
        private int GetDataType(string path)
        {
            if (String.IsNullOrEmpty(path)) return 0;

            if (path.Contains(".tex"))
            {
                return 4;
            }

            if (path.Contains(".mdl"))
            {
                return 3;
            }

            return 2;
        }


        /// <summary>
        /// Gets a XivDataFile category for the specified path.
        /// </summary>
        /// <param name="internalPath">The internal file path</param>
        /// <returns>A XivDataFile entry for the needed dat category</returns>
        private XivDataFile GetDataFileFromPath(string internalPath)
        {
            var folderKey = internalPath.Substring(0, internalPath.IndexOf("/", StringComparison.Ordinal));

            var cats = Enum.GetValues(typeof(XivDataFile)).Cast<XivDataFile>();

            foreach (var cat in cats)
            {
                if (cat.GetFolderKey() == folderKey)
                    return cat;
            }

            throw new ArgumentException("[Dat] Could not find category for path: " + internalPath);
        }



        private async Task FixPreDawntrailImports(HashSet<string> filePaths, bool updateShaders, string source, IProgress<(int current, int total, string message)> progress)
        {
            var fixableMdlsRegex = new Regex("chara\\/.*\\.mdl");
            var fixableMdls = filePaths.Where(x => fixableMdlsRegex.Match(x).Success).ToList();

            var fixableMtrlsRegex = new Regex("chara\\/(equipment|weapon)\\/.*\\.mtrl");
            var fixableMtrls = filePaths.Where(x => fixableMtrlsRegex.Match(x).Success).ToList();

            var _mtrl = new Mtrl(XivCache.GameInfo.GameDirectory);
            var _mdl = new Mdl(XivCache.GameInfo.GameDirectory);

            var idx = 0;
            var total = fixableMtrls.Count;
            foreach(var path in fixableMtrls)
            {

                progress?.Report((idx, total, "Fixing Pre-Dawntrail Materials..."));
                await _mtrl.FixPreDawntrailMaterial(path, updateShaders, source);
                idx++;
            }

            idx = 0;
            total = fixableMdls.Count;
            foreach (var path in fixableMdls)
            {
                progress?.Report((idx, total, "Fixing Pre-Dawntrail Models..."));
                await _mdl.FixPreDawntrailMdl(path, source);
            }
        }
    }
}