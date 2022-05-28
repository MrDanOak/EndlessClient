﻿using AutomaticTypeMapper;
using EOLib.Domain.Character;
using EOLib.Domain.Login;
using EOLib.Net;
using EOLib.Net.Handlers;

namespace EOLib.PacketHandlers
{
    [AutoMappedType]
    public class SpellTrainingHandler : InGameOnlyPacketHandler
    {
        private readonly ICharacterRepository _characterRepository;
        private readonly ICharacterInventoryRepository _characterInventoryRepository;

        public override PacketFamily Family => PacketFamily.StatSkill;

        public override PacketAction Action => PacketAction.Accept;

        public SpellTrainingHandler(IPlayerInfoProvider playerInfoProvider,
                                    ICharacterRepository characterRepository,
                                    ICharacterInventoryRepository characterInventoryRepository)
            : base(playerInfoProvider)
        {
            _characterRepository = characterRepository;
            _characterInventoryRepository = characterInventoryRepository;
        }

        public override bool HandlePacket(IPacket packet)
        {
            var skillPoints = packet.ReadShort();
            var spellId = packet.ReadShort();
            var spellLevel = packet.ReadShort();

            _characterInventoryRepository.SpellInventory.RemoveWhere(x => x.ID == spellId);
            _characterInventoryRepository.SpellInventory.Add(new InventorySpell(spellId, spellLevel));

            var stats = _characterRepository.MainCharacter.Stats.WithNewStat(CharacterStat.SkillPoints, skillPoints);
            _characterRepository.MainCharacter = _characterRepository.MainCharacter.WithStats(stats);

            return true;
        }
    }
}