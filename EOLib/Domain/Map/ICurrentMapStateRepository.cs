﻿// Original Work Copyright (c) Ethan Moffat 2014-2016
// This file is subject to the GPL v2 License
// For additional details, see the LICENSE file

using System.Collections.Generic;
using EOLib.Domain.Character;
using EOLib.Domain.NPC;

namespace EOLib.Domain.Map
{
    public interface ICurrentMapStateRepository
    {
        short CurrentMapID { get; set; }

        List<ICharacter> Characters { get; set; }

        List<IMapNPC> NPCs { get; set; }

        List<IMapItem> MapItems { get; set; }
        
        bool ShowMiniMap { get; set; }
    }

    public interface ICurrentMapStateProvider
    {
        short CurrentMapID { get; }

        IReadOnlyList<ICharacter> Characters { get; }

        IReadOnlyList<IMapNPC> NPCs { get; }

        IReadOnlyList<IMapItem> MapItems { get; }

        bool ShowMiniMap { get; }
    }

    public class CurrentMapStateRepository : ICurrentMapStateRepository, ICurrentMapStateProvider
    {
        public short CurrentMapID { get; set; }

        public List<ICharacter> Characters { get; set; }

        public List<IMapNPC> NPCs { get; set; }

        public List<IMapItem> MapItems { get; set; }

        public bool ShowMiniMap { get; set; }

        IReadOnlyList<ICharacter> ICurrentMapStateProvider.Characters { get { return Characters; } }

        IReadOnlyList<IMapNPC> ICurrentMapStateProvider.NPCs { get { return NPCs; } }

        IReadOnlyList<IMapItem> ICurrentMapStateProvider.MapItems { get { return MapItems; } }

        public CurrentMapStateRepository()
        {
            Characters = new List<ICharacter>();
            NPCs = new List<IMapNPC>();
            MapItems = new List<IMapItem>();
        }
    }
}
