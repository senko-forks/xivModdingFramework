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

using System;
using xivModdingFramework.Items.Interfaces;

namespace xivModdingFramework.Items.DataContainers
{
    /// <summary>
    /// This class holds information for items in the Mount Category
    /// </summary>
    public class XivMount : IItemModel
    {
        /// <summary>
        /// The mount name
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The Main Category
        /// </summary>
        /// <remarks>
        /// For Mounts the Main Category is "Companions"
        /// </remarks>
        public string Category { get; set; }

        /// <summary>
        /// The item Category
        /// </summary>
        /// <remarks>
        /// For Mounts the item Category is "Mounts"
        /// </remarks>
        public string ItemCategory { get; set; }

        /// <summary>
        /// The item SubCategory
        /// </summary>
        /// <remarks>
        /// This is currently not used for the Mount Category, but may be used in the future
        /// </remarks>
        public string ItemSubCategory { get; set; }

        /// <summary>
        /// The Primary Model Information of the Mount Item
        /// </summary>
        public XivModelInfo PrimaryModelInfo { get; set; }


        public int CompareTo(object obj)
        {
            return string.Compare(Name, ((XivMount)obj).Name, StringComparison.Ordinal);
        }
    }
}