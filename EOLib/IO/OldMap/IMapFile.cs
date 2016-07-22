﻿// Original Work Copyright (c) Ethan Moffat 2014-2016
// This file is subject to the GPL v2 License
// For additional details, see the LICENSE file

using System.Collections.Generic;
using EOLib.IO.Map;

namespace EOLib.IO.OldMap
{
    public interface IMapFile
    {
        IMapFileProperties Properties { get; }

        IReadOnly2DArray<TileSpec> Tiles { get; }
        IReadOnly2DArray<Warp> Warps { get; }
        IReadOnlyDictionary<MapLayer, IReadOnly2DArray<int>> GFX { get; }

        List<NPCSpawn> NPCSpawns { get; }
        List<byte[]> Unknowns { get; }
        List<MapChest> Chests { get; }
        List<MapSign> Signs { get; }

        void Load(string fileName);
    }
}
