﻿/*
 * Copyright (C) 2012-2019 CypherCore <http://github.com/CypherCore>
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using Framework.Collections;
using Framework.Constants;
using Framework.Database;
using Game.DataStorage;
using Game.Loots;
using Game.Network;
using Game.Network.Packets;
using Game.Spells;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Game.Entities
{
    public class Item : WorldObject
    {
        public Item() : base(false)
        {
            ObjectTypeMask |= TypeMask.Item;
            ObjectTypeId = TypeId.Item;

            m_itemData = new ItemData();

            uState = ItemUpdateState.New;
            uQueuePos = -1;
            m_lastPlayedTimeUpdate = Time.UnixTime;

            loot = new Loot();
        }

        public virtual bool Create(ulong guidlow, uint itemId, ItemContext context, Player owner)
        {
            _Create(ObjectGuid.Create(HighGuid.Item, guidlow));

            SetEntry(itemId);
            SetObjectScale(1.0f);

            if (owner)
            {
                SetOwnerGUID(owner.GetGUID());
                SetContainedIn(owner.GetGUID());
            }

            ItemTemplate itemProto = Global.ObjectMgr.GetItemTemplate(itemId);
            if (itemProto == null)
                return false;

            _bonusData = new BonusData(itemProto);
            SetCount(1);
            SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.MaxDurability), itemProto.MaxDurability);
            SetDurability(itemProto.MaxDurability);

            for (int i = 0; i < itemProto.Effects.Count; ++i)
            {
                if (i < 5)
                    SetSpellCharges(i, itemProto.Effects[i].Charges);
            }

            SetExpiration(itemProto.GetDuration());
            SetCreatePlayedTime(0);
            SetContext(context);

            if (itemProto.GetArtifactID() != 0)
            {
                InitArtifactPowers(itemProto.GetArtifactID(), 0);
                foreach (ArtifactAppearanceRecord artifactAppearance in CliDB.ArtifactAppearanceStorage.Values)
                {
                    ArtifactAppearanceSetRecord artifactAppearanceSet = CliDB.ArtifactAppearanceSetStorage.LookupByKey(artifactAppearance.ArtifactAppearanceSetID);
                    if (artifactAppearanceSet != null)
                    {
                        if (itemProto.GetArtifactID() != artifactAppearanceSet.ArtifactID)
                            continue;

                        PlayerConditionRecord playerCondition = CliDB.PlayerConditionStorage.LookupByKey(artifactAppearance.UnlockPlayerConditionID);
                        if (playerCondition != null)
                            if (!owner || !ConditionManager.IsPlayerMeetingCondition(owner, playerCondition))
                                continue;

                        SetModifier(ItemModifier.ArtifactAppearanceId, artifactAppearance.Id);
                        SetAppearanceModId(artifactAppearance.ItemAppearanceModifierID);
                        break;
                    }
                }

                CheckArtifactRelicSlotUnlock(owner != null ? owner : GetOwner());
            }
            return true;
        }

        public bool IsNotEmptyBag()
        {
            Bag bag = ToBag();
            if (bag != null)
                return !bag.IsEmpty();

            return false;
        }

        public void UpdateDuration(Player owner, uint diff)
        {
            uint duration = m_itemData.Expiration;
            if (duration == 0)
                return;

            Log.outDebug(LogFilter.Player, "Item.UpdateDuration Item (Entry: {0} Duration {1} Diff {2})", GetEntry(), duration, diff);

            if (duration <= diff)
            {
                Global.ScriptMgr.OnItemExpire(owner, GetTemplate());
                owner.DestroyItem(GetBagSlot(), GetSlot(), true);
                return;
            }

            SetExpiration(duration - diff);
            SetState(ItemUpdateState.Changed, owner);                          // save new time in database
        }

        public virtual void SaveToDB(SQLTransaction trans)
        {
            PreparedStatement stmt;
            switch (uState)
            {
                case ItemUpdateState.New:
                case ItemUpdateState.Changed:
                    {
                        byte index = 0;
                        stmt = DB.Characters.GetPreparedStatement(uState == ItemUpdateState.New ? CharStatements.REP_ITEM_INSTANCE : CharStatements.UPD_ITEM_INSTANCE);
                        stmt.AddValue(index, GetEntry());
                        stmt.AddValue(++index, GetOwnerGUID().GetCounter());
                        stmt.AddValue(++index, GetCreator().GetCounter());
                        stmt.AddValue(++index, GetGiftCreator().GetCounter());
                        stmt.AddValue(++index, GetCount());
                        stmt.AddValue(++index, (uint)m_itemData.Expiration);

                        StringBuilder ss = new StringBuilder();
                        for (byte i = 0; i < ItemConst.MaxSpells; ++i)
                            ss.AppendFormat("{0} ", GetSpellCharges(i));

                        stmt.AddValue(++index, ss.ToString());
                        stmt.AddValue(++index, (uint)m_itemData.DynamicFlags);

                        ss.Clear();
                        for (EnchantmentSlot slot = 0; slot < EnchantmentSlot.Max; ++slot)
                            ss.AppendFormat("{0} {1} {2} ", GetEnchantmentId(slot), GetEnchantmentDuration(slot), GetEnchantmentCharges(slot));

                        stmt.AddValue(++index, ss.ToString());
                        stmt.AddValue(++index, m_randomBonusListId);
                        stmt.AddValue(++index, (uint)m_itemData.Durability);
                        stmt.AddValue(++index, (uint)m_itemData.CreatePlayedTime);
                        stmt.AddValue(++index, m_text);
                        stmt.AddValue(++index, GetModifier(ItemModifier.BattlePetSpeciesId));
                        stmt.AddValue(++index, GetModifier(ItemModifier.BattlePetBreedData));
                        stmt.AddValue(++index, GetModifier(ItemModifier.BattlePetLevel));
                        stmt.AddValue(++index, GetModifier(ItemModifier.BattlePetDisplayId));
                        stmt.AddValue(++index, (byte)m_itemData.Context);

                        ss.Clear();

                        foreach (int bonusListID in (List<uint>)m_itemData.BonusListIDs)
                            ss.Append(bonusListID + ' ');

                        stmt.AddValue(++index, ss.ToString());
                        stmt.AddValue(++index, GetGUID().GetCounter());

                        DB.Characters.Execute(stmt);

                        if ((uState == ItemUpdateState.Changed) && HasItemFlag(ItemFieldFlags.Wrapped))
                        {
                            stmt = DB.Characters.GetPreparedStatement(CharStatements.UPD_GIFT_OWNER);
                            stmt.AddValue(0, GetOwnerGUID().GetCounter());
                            stmt.AddValue(1, GetGUID().GetCounter());
                            DB.Characters.Execute(stmt);
                        }

                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_GEMS);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        if (m_itemData.Gems.Size() != 0)
                        {
                            stmt = DB.Characters.GetPreparedStatement(CharStatements.INS_ITEM_INSTANCE_GEMS);
                            stmt.AddValue(0, GetGUID().GetCounter());
                            int i = 0;
                            int gemFields = 4;

                            foreach (SocketedGem gemData in m_itemData.Gems)
                            {
                                if (gemData.ItemId != 0)
                                {
                                    stmt.AddValue(1 + i * gemFields, (uint)gemData.ItemId);
                                    StringBuilder gemBonusListIDs = new StringBuilder();
                                    foreach (ushort bonusListID in gemData.BonusListIDs)
                                    {
                                        if (bonusListID != 0)
                                            gemBonusListIDs.AppendFormat("{0} ", bonusListID);
                                    }

                                    stmt.AddValue(2 + i * gemFields, gemBonusListIDs.ToString());
                                    stmt.AddValue(3 + i * gemFields, (byte)gemData.Context);
                                    stmt.AddValue(4 + i * gemFields, m_gemScalingLevels[i]);
                                }
                                else
                                {
                                    stmt.AddValue(1 + i * gemFields, 0);
                                    stmt.AddValue(2 + i * gemFields, "");
                                    stmt.AddValue(3 + i * gemFields, 0);
                                    stmt.AddValue(4 + i * gemFields, 0);
                                }
                                ++i;
                            }

                            for (; i < ItemConst.MaxGemSockets; ++i)
                            {
                                stmt.AddValue(1 + i * gemFields, 0);
                                stmt.AddValue(2 + i * gemFields, "");
                                stmt.AddValue(3 + i * gemFields, 0);
                                stmt.AddValue(4 + i * gemFields, 0);
                            }
                            trans.Append(stmt);
                        }

                        ItemModifier[] transmogMods =
                        {
                            ItemModifier.TransmogAppearanceAllSpecs,
                            ItemModifier.TransmogAppearanceSpec1,
                            ItemModifier.TransmogAppearanceSpec2,
                            ItemModifier.TransmogAppearanceSpec3,
                            ItemModifier.TransmogAppearanceSpec4,

                            ItemModifier.EnchantIllusionAllSpecs,
                            ItemModifier.EnchantIllusionSpec1,
                            ItemModifier.EnchantIllusionSpec2,
                            ItemModifier.EnchantIllusionSpec3,
                            ItemModifier.EnchantIllusionSpec4,
                        };

                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_TRANSMOG);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        if (transmogMods.Any(modifier => GetModifier(modifier) != 0))
                        {
                            stmt = DB.Characters.GetPreparedStatement(CharStatements.INS_ITEM_INSTANCE_TRANSMOG);
                            stmt.AddValue(0, GetGUID().GetCounter());
                            stmt.AddValue(1, GetModifier(ItemModifier.TransmogAppearanceAllSpecs));
                            stmt.AddValue(2, GetModifier(ItemModifier.TransmogAppearanceSpec1));
                            stmt.AddValue(3, GetModifier(ItemModifier.TransmogAppearanceSpec2));
                            stmt.AddValue(4, GetModifier(ItemModifier.TransmogAppearanceSpec3));
                            stmt.AddValue(5, GetModifier(ItemModifier.TransmogAppearanceSpec4));
                            stmt.AddValue(6, GetModifier(ItemModifier.EnchantIllusionAllSpecs));
                            stmt.AddValue(7, GetModifier(ItemModifier.EnchantIllusionSpec1));
                            stmt.AddValue(8, GetModifier(ItemModifier.EnchantIllusionSpec2));
                            stmt.AddValue(9, GetModifier(ItemModifier.EnchantIllusionSpec3));
                            stmt.AddValue(10, GetModifier(ItemModifier.EnchantIllusionSpec4));
                            trans.Append(stmt);
                        }

                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_ARTIFACT);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_ARTIFACT_POWERS);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        if (GetTemplate().GetArtifactID() != 0)
                        {
                            stmt = DB.Characters.GetPreparedStatement(CharStatements.INS_ITEM_INSTANCE_ARTIFACT);
                            stmt.AddValue(0, GetGUID().GetCounter());
                            stmt.AddValue(1, (ulong)m_itemData.ArtifactXP);
                            stmt.AddValue(2, GetModifier(ItemModifier.ArtifactAppearanceId));
                            stmt.AddValue(3, GetModifier(ItemModifier.ArtifactTier));
                            trans.Append(stmt);

                            foreach (ArtifactPower artifactPower in m_itemData.ArtifactPowers)
                            {
                                stmt = DB.Characters.GetPreparedStatement(CharStatements.INS_ITEM_INSTANCE_ARTIFACT_POWERS);
                                stmt.AddValue(0, GetGUID().GetCounter());
                                stmt.AddValue(1, artifactPower.ArtifactPowerId);
                                stmt.AddValue(2, artifactPower.PurchasedRank);
                                trans.Append(stmt);
                            }
                        }

                        ItemModifier[] modifiersTable =
                        {
                            ItemModifier.TimewalkerLevel,
                            ItemModifier.ArtifactKnowledgeLevel
                        };

                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_MODIFIERS);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        if (modifiersTable.Any(modifier => GetModifier(modifier) != 0))
                        {
                            stmt = DB.Characters.GetPreparedStatement(CharStatements.INS_ITEM_INSTANCE_MODIFIERS);
                            stmt.AddValue(0, GetGUID().GetCounter());
                            stmt.AddValue(1, GetModifier(ItemModifier.TimewalkerLevel));
                            stmt.AddValue(2, GetModifier(ItemModifier.ArtifactKnowledgeLevel));
                            trans.Append(stmt);
                        }
                        break;
                    }
                case ItemUpdateState.Removed:
                    {
                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_GEMS);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_TRANSMOG);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_ARTIFACT);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_ARTIFACT_POWERS);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_MODIFIERS);
                        stmt.AddValue(0, GetGUID().GetCounter());
                        trans.Append(stmt);

                        if (HasItemFlag(ItemFieldFlags.Wrapped))
                        {
                            stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_GIFT);
                            stmt.AddValue(0, GetGUID().GetCounter());
                            trans.Append(stmt);
                        }

                        // Delete the items if this is a container
                        if (!loot.IsLooted())
                            ItemContainerDeleteLootMoneyAndLootItemsFromDB();

                        Dispose();
                        return;
                    }
                case ItemUpdateState.Unchanged:
                    break;
            }

            SetState(ItemUpdateState.Unchanged);
        }

        public virtual bool LoadFromDB(ulong guid, ObjectGuid ownerGuid, SQLFields fields, uint entry)
        {
            // create item before any checks for store correct guid
            // and allow use "FSetState(ITEM_REMOVED); SaveToDB();" for deleting item from DB
            _Create(ObjectGuid.Create(HighGuid.Item, guid));

            SetEntry(entry);
            SetObjectScale(1.0f);

            ItemTemplate proto = GetTemplate();
            if (proto == null)
                return false;

            _bonusData = new BonusData(proto);

            // set owner (not if item is only loaded for gbank/auction/mail
            if (!ownerGuid.IsEmpty())
                SetOwnerGUID(ownerGuid);

            uint itemFlags = fields.Read<uint>(7);
            bool need_save = false;
            ulong creator = fields.Read<ulong>(2);
            if (creator != 0)
            {
                if (!Convert.ToBoolean(itemFlags & (int)ItemFieldFlags.Child))
                    SetCreator(ObjectGuid.Create(HighGuid.Player, creator));
                else
                    SetCreator(ObjectGuid.Create(HighGuid.Item, creator));
            }

            ulong giftCreator = fields.Read<ulong>(3);
            if (giftCreator != 0)
                SetGiftCreator(ObjectGuid.Create(HighGuid.Player, giftCreator));

            SetCount(fields.Read<uint>(4));

            uint duration = fields.Read<uint>(5);
            SetExpiration(duration);
            // update duration if need, and remove if not need
            if (proto.GetDuration() != duration)
            {
                SetExpiration(proto.GetDuration());
                need_save = true;
            }

            var tokens = new StringArray(fields.Read<string>(6), ' ');
            if (tokens.Length == ItemConst.MaxProtoSpells)
            {
                for (byte i = 0; i < ItemConst.MaxProtoSpells; ++i)
                {
                    if (int.TryParse(tokens[i], out int value))
                        SetSpellCharges(i, value);
                }
            }

            SetItemFlags((ItemFieldFlags)itemFlags);

            uint durability = fields.Read<uint>(10);
            SetDurability(durability);
            // update max durability (and durability) if need
            SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.MaxDurability), proto.MaxDurability);
            if (durability > proto.MaxDurability)
            {
                SetDurability(proto.MaxDurability);
                need_save = true;
            }

            SetCreatePlayedTime(fields.Read<uint>(11));
            SetText(fields.Read<string>(12));

            SetModifier(ItemModifier.BattlePetSpeciesId, fields.Read<uint>(13));
            SetModifier(ItemModifier.BattlePetBreedData, fields.Read<uint>(14));
            SetModifier(ItemModifier.BattlePetLevel, fields.Read<ushort>(14));
            SetModifier(ItemModifier.BattlePetDisplayId, fields.Read<uint>(16));

            SetContext((ItemContext)fields.Read<byte>(17));

            var bonusListString = new StringArray(fields.Read<string>(18), ' ');
            List<uint> bonusListIDs = new List<uint>();
            for (var i = 0; i < bonusListString.Length; ++i)
            {
                if (uint.TryParse(bonusListString[i], out uint bonusListID))
                    bonusListIDs.Add(bonusListID);
            }
            SetBonuses(bonusListIDs);

            SetModifier(ItemModifier.TransmogAppearanceAllSpecs, fields.Read<uint>(19));
            SetModifier(ItemModifier.TransmogAppearanceSpec1, fields.Read<uint>(20));
            SetModifier(ItemModifier.TransmogAppearanceSpec2, fields.Read<uint>(21));
            SetModifier(ItemModifier.TransmogAppearanceSpec3, fields.Read<uint>(22));
            SetModifier(ItemModifier.TransmogAppearanceSpec4, fields.Read<uint>(23));

            SetModifier(ItemModifier.EnchantIllusionAllSpecs, fields.Read<uint>(24));
            SetModifier(ItemModifier.EnchantIllusionSpec1, fields.Read<uint>(25));
            SetModifier(ItemModifier.EnchantIllusionSpec2, fields.Read<uint>(26));
            SetModifier(ItemModifier.EnchantIllusionSpec3, fields.Read<uint>(27));
            SetModifier(ItemModifier.EnchantIllusionSpec4, fields.Read<uint>(28));

            int gemFields = 4;
            ItemDynamicFieldGems[] gemData = new ItemDynamicFieldGems[ItemConst.MaxGemSockets];
            for (int i = 0; i < ItemConst.MaxGemSockets; ++i)
            {
                gemData[i] = new ItemDynamicFieldGems();
                gemData[i].ItemId = fields.Read<uint>(29 + i * gemFields);
                var gemBonusListIDs = new StringArray(fields.Read<string>(30 + i * gemFields), ' ');
                if (!gemBonusListIDs.IsEmpty())
                {
                    uint b = 0;
                    foreach (string token in gemBonusListIDs)
                    {
                        if (uint.TryParse(token, out uint bonusListID) && bonusListID != 0)
                            gemData[i].BonusListIDs[b++] = (ushort)bonusListID;
                    }
                }

                gemData[i].Context = fields.Read<byte>(31 + i * gemFields);
                if (gemData[i].ItemId != 0)
                    SetGem((ushort)i, gemData[i], fields.Read<uint>(32 + i * gemFields));
            }

            SetModifier(ItemModifier.TimewalkerLevel, fields.Read<uint>(41));
            SetModifier(ItemModifier.ArtifactKnowledgeLevel, fields.Read<uint>(42));

            // Enchants must be loaded after all other bonus/scaling data
            var enchantmentTokens = new StringArray(fields.Read<string>(8), ' ');
            if (enchantmentTokens.Length == (int)EnchantmentSlot.Max * (int)EnchantmentOffset.Max)
            {
                for (int i = 0; i < (int)EnchantmentSlot.Max; ++i)
                {
                    ItemEnchantment enchantmentField = m_itemData.ModifyValue(m_itemData.Enchantment, i);
                    SetUpdateFieldValue(enchantmentField.ModifyValue(enchantmentField.ID), uint.Parse(enchantmentTokens[i * (int)EnchantmentOffset.Max + 0]));
                    SetUpdateFieldValue(enchantmentField.ModifyValue(enchantmentField.Duration), uint.Parse(enchantmentTokens[i * (int)EnchantmentOffset.Max + 1]));
                    SetUpdateFieldValue(enchantmentField.ModifyValue(enchantmentField.Charges), short.Parse(enchantmentTokens[i * (int)EnchantmentOffset.Max + 2]));
                }
            }

            m_randomBonusListId = fields.Read<uint>(10);

            // Remove bind flag for items vs NO_BIND set
            if (IsSoulBound() && GetBonding() == ItemBondingType.None)
            {
                RemoveItemFlag(ItemFieldFlags.Soulbound);
                need_save = true;
            }

            if (need_save)                                           // normal item changed state set not work at loading
            {
                byte index = 0;
                PreparedStatement stmt = DB.Characters.GetPreparedStatement(CharStatements.UPD_ITEM_INSTANCE_ON_LOAD);
                stmt.AddValue(index++, (uint)m_itemData.Expiration);
                stmt.AddValue(index++, (uint)m_itemData.DynamicFlags);
                stmt.AddValue(index++, (uint)m_itemData.Durability);
                stmt.AddValue(index++, guid);
                DB.Characters.Execute(stmt);
            }
            return true;
        }

        public void LoadArtifactData(Player owner, ulong xp, uint artifactAppearanceId, uint artifactTier, List<ArtifactPowerData> powers)
        {
            for (byte i = 0; i <= artifactTier; ++i)
                InitArtifactPowers(GetTemplate().GetArtifactID(), i);

            SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.ArtifactXP), xp);
            SetModifier(ItemModifier.ArtifactAppearanceId, artifactAppearanceId);
            SetModifier(ItemModifier.ArtifactTier, artifactTier);

            ArtifactAppearanceRecord artifactAppearance = CliDB.ArtifactAppearanceStorage.LookupByKey(artifactAppearanceId);
            if (artifactAppearance != null)
                SetAppearanceModId(artifactAppearance.ItemAppearanceModifierID);

            byte totalPurchasedRanks = 0;
            foreach (ArtifactPowerData power in powers)
            {
                power.CurrentRankWithBonus += power.PurchasedRank;
                totalPurchasedRanks += power.PurchasedRank;

                ArtifactPowerRecord artifactPower = CliDB.ArtifactPowerStorage.LookupByKey(power.ArtifactPowerId);
                for (var e = EnchantmentSlot.Sock1; e <= EnchantmentSlot.Sock3; ++e)
                {
                    SpellItemEnchantmentRecord enchant = CliDB.SpellItemEnchantmentStorage.LookupByKey(GetEnchantmentId(e));
                    if (enchant != null)
                    {
                        for (uint i = 0; i < ItemConst.MaxItemEnchantmentEffects; ++i)
                        {
                            switch (enchant.Effect[i])
                            {
                                case ItemEnchantmentType.ArtifactPowerBonusRankByType:
                                    if (artifactPower.Label == enchant.EffectArg[i])
                                        power.CurrentRankWithBonus += (byte)enchant.EffectPointsMin[i];
                                    break;
                                case ItemEnchantmentType.ArtifactPowerBonusRankByID:
                                    if (artifactPower.Id == enchant.EffectArg[i])
                                        power.CurrentRankWithBonus += (byte)enchant.EffectPointsMin[i];
                                    break;
                                case ItemEnchantmentType.ArtifactPowerBonusRankPicker:
                                    if (_bonusData.GemRelicType[e - EnchantmentSlot.Sock1] != -1)
                                    {
                                        ArtifactPowerPickerRecord artifactPowerPicker = CliDB.ArtifactPowerPickerStorage.LookupByKey(enchant.EffectArg[i]);
                                        if (artifactPowerPicker != null)
                                        {
                                            PlayerConditionRecord playerCondition = CliDB.PlayerConditionStorage.LookupByKey(artifactPowerPicker.PlayerConditionID);
                                            if (playerCondition == null || (owner != null && ConditionManager.IsPlayerMeetingCondition(owner, playerCondition)))
                                                if (artifactPower.Label == _bonusData.GemRelicType[e - EnchantmentSlot.Sock1])
                                                    power.CurrentRankWithBonus += (byte)enchant.EffectPointsMin[i];
                                        }
                                    }
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                }

                SetArtifactPower((ushort)power.ArtifactPowerId, power.PurchasedRank, power.CurrentRankWithBonus);
            }

            foreach (ArtifactPowerData power in powers)
            {
                ArtifactPowerRecord scaledArtifactPowerEntry = CliDB.ArtifactPowerStorage.LookupByKey(power.ArtifactPowerId);
                if (!scaledArtifactPowerEntry.Flags.HasAnyFlag(ArtifactPowerFlag.ScalesWithNumPowers))
                    continue;

                SetArtifactPower((ushort)power.ArtifactPowerId, power.PurchasedRank, (byte)(totalPurchasedRanks + 1));
            }

            CheckArtifactRelicSlotUnlock(owner);
        }

        public void CheckArtifactRelicSlotUnlock(Player owner)
        {
            if (!owner)
                return;

            byte artifactId = GetTemplate().GetArtifactID();
            if (artifactId == 0)
                return;

            foreach (ArtifactUnlockRecord artifactUnlock in CliDB.ArtifactUnlockStorage.Values)
                if (artifactUnlock.ArtifactID == artifactId)
                    if (owner.MeetPlayerCondition(artifactUnlock.PlayerConditionID))
                        AddBonuses(artifactUnlock.ItemBonusListID);
        }

        public static void DeleteFromDB(SQLTransaction trans, ulong itemGuid)
        {
            PreparedStatement stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE);
            stmt.AddValue(0, itemGuid);
            DB.Characters.ExecuteOrAppend(trans, stmt);

            stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_GEMS);
            stmt.AddValue(0, itemGuid);
            DB.Characters.ExecuteOrAppend(trans, stmt);

            stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_TRANSMOG);
            stmt.AddValue(0, itemGuid);
            DB.Characters.ExecuteOrAppend(trans, stmt);

            stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_ARTIFACT);
            stmt.AddValue(0, itemGuid);
            DB.Characters.ExecuteOrAppend(trans, stmt);

            stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_ARTIFACT_POWERS);
            stmt.AddValue(0, itemGuid);
            DB.Characters.ExecuteOrAppend(trans, stmt);

            stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_INSTANCE_MODIFIERS);
            stmt.AddValue(0, itemGuid);
            DB.Characters.ExecuteOrAppend(trans, stmt);

            stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_GIFT);
            stmt.AddValue(0, itemGuid);
            DB.Characters.ExecuteOrAppend(trans, stmt);
        }

        public virtual void DeleteFromDB(SQLTransaction trans)
        {
            DeleteFromDB(trans, GetGUID().GetCounter());

            // Delete the items if this is a container
            if (!loot.IsLooted())
                ItemContainerDeleteLootMoneyAndLootItemsFromDB();
        }

        public static void DeleteFromInventoryDB(SQLTransaction trans, ulong itemGuid)
        {
            PreparedStatement stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_CHAR_INVENTORY_BY_ITEM);
            stmt.AddValue(0, itemGuid);
            trans.Append(stmt);
        }

        public void DeleteFromInventoryDB(SQLTransaction trans)
        {
            DeleteFromInventoryDB(trans, GetGUID().GetCounter());
        }

        public ItemTemplate GetTemplate()
        {
            return Global.ObjectMgr.GetItemTemplate(GetEntry());
        }

        public Player GetOwner()
        {
            return Global.ObjAccessor.FindPlayer(GetOwnerGUID());
        }

        public SkillType GetSkill()
        {
            ItemTemplate proto = GetTemplate();
            return proto.GetSkill();
        }

        public void SetItemRandomBonusList(uint bonusListId)
        {
            if (bonusListId == 0)
                return;

            AddBonuses(bonusListId);
        }

        public void SetState(ItemUpdateState state, Player forplayer = null)
        {
            if (uState == ItemUpdateState.New && state == ItemUpdateState.Removed)
            {
                // pretend the item never existed
                if (forplayer)
                {
                    RemoveItemFromUpdateQueueOf(this, forplayer);
                    forplayer.DeleteRefundReference(GetGUID());
                }
                return;
            }
            if (state != ItemUpdateState.Unchanged)
            {
                // new items must stay in new state until saved
                if (uState != ItemUpdateState.New)
                    uState = state;

                if (forplayer)
                    AddItemToUpdateQueueOf(this, forplayer);
            }
            else
            {
                // unset in queue
                // the item must be removed from the queue manually
                uQueuePos = -1;
                uState = ItemUpdateState.Unchanged;
            }
        }

        static void AddItemToUpdateQueueOf(Item item, Player player)
        {
            if (item.IsInUpdateQueue())
                return;

            Cypher.Assert(player != null);

            if (player.GetGUID() != item.GetOwnerGUID())
            {
                Log.outError(LogFilter.Player, "Item.AddToUpdateQueueOf - Owner's guid ({0}) and player's guid ({1}) don't match!", item.GetOwnerGUID(), player.GetGUID().ToString());
                return;
            }

            if (player.m_itemUpdateQueueBlocked)
                return;

            player.ItemUpdateQueue.Add(item);
            item.uQueuePos = player.ItemUpdateQueue.Count - 1;
        }

        public static void RemoveItemFromUpdateQueueOf(Item item, Player player)
        {
            if (!item.IsInUpdateQueue())
                return;

            Cypher.Assert(player != null);

            if (player.GetGUID() != item.GetOwnerGUID())
            {
                Log.outError(LogFilter.Player, "Item.RemoveFromUpdateQueueOf - Owner's guid ({0}) and player's guid ({1}) don't match!", item.GetOwnerGUID().ToString(), player.GetGUID().ToString());
                return;
            }

            if (player.m_itemUpdateQueueBlocked)
                return;

            player.ItemUpdateQueue[item.uQueuePos] = null;
            item.uQueuePos = -1;
        }

        public byte GetBagSlot()
        {
            return m_container != null ? m_container.GetSlot() : InventorySlots.Bag0;
        }

        public bool IsEquipped() { return !IsInBag() && m_slot < EquipmentSlot.End; }

        public bool CanBeTraded(bool mail = false, bool trade = false)
        {
            if (m_lootGenerated)
                return false;

            if ((!mail || !IsBoundAccountWide()) && (IsSoulBound() && (!HasItemFlag(ItemFieldFlags.BopTradeable) || !trade)))
                return false;

            if (IsBag() && (Player.IsBagPos(GetPos()) || !ToBag().IsEmpty()))
                return false;

            Player owner = GetOwner();
            if (owner != null)
            {
                if (owner.CanUnequipItem(GetPos(), false) != InventoryResult.Ok)
                    return false;
                if (owner.GetLootGUID() == GetGUID())
                    return false;
            }

            if (IsBoundByEnchant())
                return false;

            return true;
        }

        public void SetCount(uint value)
        {
            SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.StackCount), value);

            Player player = GetOwner();
            if (player)
            {
                TradeData tradeData = player.GetTradeData();
                if (tradeData != null)
                {
                    TradeSlots slot = tradeData.GetTradeSlotForItem(GetGUID());

                    if (slot != TradeSlots.Invalid)
                        tradeData.SetItem(slot, this, true);
                }
            }
        }

        bool HasEnchantRequiredSkill(Player player)
        {
            // Check all enchants for required skill
            for (var enchant_slot = EnchantmentSlot.Perm; enchant_slot < EnchantmentSlot.Max; ++enchant_slot)
            {
                uint enchant_id = GetEnchantmentId(enchant_slot);
                if (enchant_id != 0)
                {
                    SpellItemEnchantmentRecord enchantEntry = CliDB.SpellItemEnchantmentStorage.LookupByKey(enchant_id);
                    if (enchantEntry != null)
                        if (enchantEntry.RequiredSkillID != 0 && player.GetSkillValue((SkillType)enchantEntry.RequiredSkillID) < enchantEntry.RequiredSkillRank)
                            return false;
                }
            }

            return true;
        }

        uint GetEnchantRequiredLevel()
        {
            uint level = 0;

            // Check all enchants for required level
            for (var enchant_slot = EnchantmentSlot.Perm; enchant_slot < EnchantmentSlot.Max; ++enchant_slot)
            {
                uint enchant_id = GetEnchantmentId(enchant_slot);
                if (enchant_id != 0)
                {
                    var enchantEntry = CliDB.SpellItemEnchantmentStorage.LookupByKey(enchant_id);
                    if (enchantEntry != null)
                        if (enchantEntry.MinLevel > level)
                            level = enchantEntry.MinLevel;
                }
            }

            return level;
        }

        bool IsBoundByEnchant()
        {
            // Check all enchants for soulbound
            for (var enchant_slot = EnchantmentSlot.Perm; enchant_slot < EnchantmentSlot.Max; ++enchant_slot)
            {
                uint enchant_id = GetEnchantmentId(enchant_slot);
                if (enchant_id != 0)
                {
                    var enchantEntry = CliDB.SpellItemEnchantmentStorage.LookupByKey(enchant_id);
                    if (enchantEntry != null)
                        if (enchantEntry.Flags.HasAnyFlag(EnchantmentSlotMask.CanSouldBound))
                            return true;
                }
            }

            return false;
        }

        public InventoryResult CanBeMergedPartlyWith(ItemTemplate proto)
        {
            // not allow merge looting currently items
            if (m_lootGenerated)
                return InventoryResult.LootGone;

            // check item type
            if (GetEntry() != proto.GetId())
                return InventoryResult.CantStack;

            // check free space (full stacks can't be target of merge
            if (GetCount() >= proto.GetMaxStackSize())
                return InventoryResult.CantStack;

            return InventoryResult.Ok;
        }

        public bool IsFitToSpellRequirements(SpellInfo spellInfo)
        {
            ItemTemplate proto = GetTemplate();

            bool isEnchantSpell = spellInfo.HasEffect(SpellEffectName.EnchantItem) || spellInfo.HasEffect(SpellEffectName.EnchantItemTemporary) || spellInfo.HasEffect(SpellEffectName.EnchantItemPrismatic);
            if ((int)spellInfo.EquippedItemClass != -1)                 // -1 == any item class
            {
                if (isEnchantSpell && proto.GetFlags3().HasAnyFlag(ItemFlags3.CanStoreEnchants))
                    return true;

                if (spellInfo.EquippedItemClass != proto.GetClass())
                    return false;                                   //  wrong item class

                if (spellInfo.EquippedItemSubClassMask != 0)        // 0 == any subclass
                {
                    if ((spellInfo.EquippedItemSubClassMask & (1 << (int)proto.GetSubClass())) == 0)
                        return false;                               // subclass not present in mask
                }
            }

            if (isEnchantSpell && spellInfo.EquippedItemInventoryTypeMask != 0)       // 0 == any inventory type
            {
                // Special case - accept weapon type for main and offhand requirements
                if (proto.GetInventoryType() == InventoryType.Weapon &&
                    Convert.ToBoolean(spellInfo.EquippedItemInventoryTypeMask & (1 << (int)InventoryType.WeaponMainhand)) ||
                     Convert.ToBoolean(spellInfo.EquippedItemInventoryTypeMask & (1 << (int)InventoryType.WeaponOffhand)))
                    return true;
                else if ((spellInfo.EquippedItemInventoryTypeMask & (1 << (int)proto.GetInventoryType())) == 0)
                    return false;                                   // inventory type not present in mask
            }

            return true;
        }

        public void SetEnchantment(EnchantmentSlot slot, uint id, uint duration, uint charges, ObjectGuid caster = default)
        {
            // Better lost small time at check in comparison lost time at item save to DB.
            if ((GetEnchantmentId(slot) == id) && (GetEnchantmentDuration(slot) == duration) && (GetEnchantmentCharges(slot) == charges))
                return;

            Player owner = GetOwner();
            if (slot < EnchantmentSlot.MaxInspected)
            {
                uint oldEnchant = GetEnchantmentId(slot);
                if (oldEnchant != 0)
                    owner.GetSession().SendEnchantmentLog(GetOwnerGUID(), ObjectGuid.Empty, GetGUID(), GetEntry(), oldEnchant, (uint)slot);

                if (id != 0)
                    owner.GetSession().SendEnchantmentLog(GetOwnerGUID(), caster, GetGUID(), GetEntry(), id, (uint)slot);
            }

            ApplyArtifactPowerEnchantmentBonuses(slot, GetEnchantmentId(slot), false, owner);
            ApplyArtifactPowerEnchantmentBonuses(slot, id, true, owner);

            ItemEnchantment enchantmentField = m_itemData.ModifyValue(m_itemData.Enchantment, (int)slot);
            SetUpdateFieldValue(enchantmentField.ModifyValue(enchantmentField.ID), id);
            SetUpdateFieldValue(enchantmentField.ModifyValue(enchantmentField.Duration), duration);
            SetUpdateFieldValue(enchantmentField.ModifyValue(enchantmentField.Charges), (short)charges);
            SetState(ItemUpdateState.Changed, owner);
        }

        public void SetEnchantmentDuration(EnchantmentSlot slot, uint duration, Player owner)
        {
            if (GetEnchantmentDuration(slot) == duration)
                return;

            SetUpdateFieldValue(m_itemData.ModifyValue(m_itemData.Enchantment, (int)slot).ModifyValue((ItemEnchantment itemEnchantment) => itemEnchantment.Duration), duration);
            SetState(ItemUpdateState.Changed, owner);
            // Cannot use GetOwner() here, has to be passed as an argument to avoid freeze due to hashtable locking
        }

        public void SetEnchantmentCharges(EnchantmentSlot slot, uint charges)
        {
            if (GetEnchantmentCharges(slot) == charges)
                return;

            SetUpdateFieldValue(m_itemData.ModifyValue(m_itemData.Enchantment, (int)slot).ModifyValue((ItemEnchantment itemEnchantment) => itemEnchantment.Charges), (short)charges);
            SetState(ItemUpdateState.Changed, GetOwner());
        }

        public void ClearEnchantment(EnchantmentSlot slot)
        {
            if (GetEnchantmentId(slot) == 0)
                return;

            ItemEnchantment enchantmentField = m_itemData.ModifyValue(m_itemData.Enchantment, (int)slot);
            SetUpdateFieldValue(enchantmentField.ModifyValue(enchantmentField.ID), 0u);
            SetUpdateFieldValue(enchantmentField.ModifyValue(enchantmentField.Duration), 0u);
            SetUpdateFieldValue(enchantmentField.ModifyValue(enchantmentField.Charges), (short)0);
            SetUpdateFieldValue(enchantmentField.ModifyValue(enchantmentField.Inactive), (ushort)0);
            SetState(ItemUpdateState.Changed, GetOwner());
        }

        public SocketedGem GetGem(ushort slot)
        {
            //ASSERT(slot < MAX_GEM_SOCKETS);
            return slot < m_itemData.Gems.Size() ? m_itemData.Gems[slot] : null;
        }

        public void SetGem(ushort slot, ItemDynamicFieldGems gem, uint gemScalingLevel)
        {
            //ASSERT(slot < MAX_GEM_SOCKETS);
            m_gemScalingLevels[slot] = gemScalingLevel;
            _bonusData.GemItemLevelBonus[slot] = 0;
            ItemTemplate gemTemplate = Global.ObjectMgr.GetItemTemplate(gem.ItemId);
            if (gemTemplate != null)
            {
                GemPropertiesRecord gemProperties = CliDB.GemPropertiesStorage.LookupByKey(gemTemplate.GetGemProperties());
                if (gemProperties != null)
                {
                    SpellItemEnchantmentRecord gemEnchant = CliDB.SpellItemEnchantmentStorage.LookupByKey(gemProperties.EnchantId);
                    if (gemEnchant != null)
                    {
                        BonusData gemBonus = new BonusData(gemTemplate);
                        foreach (var bonusListId in gem.BonusListIDs)
                            gemBonus.AddBonusList(bonusListId);

                        uint gemBaseItemLevel = gemTemplate.GetBaseItemLevel();
                        ScalingStatDistributionRecord ssd = CliDB.ScalingStatDistributionStorage.LookupByKey(gemBonus.ScalingStatDistribution);
                        if (ssd != null)
                        {
                            uint scaledIlvl = (uint)Global.DB2Mgr.GetCurveValueAt(ssd.PlayerLevelToItemLevelCurveID, gemScalingLevel);
                            if (scaledIlvl != 0)
                                gemBaseItemLevel = scaledIlvl;
                        }

                        _bonusData.GemRelicType[slot] = gemBonus.RelicType;

                        for (uint i = 0; i < ItemConst.MaxItemEnchantmentEffects; ++i)
                        {
                            switch (gemEnchant.Effect[i])
                            {
                                case ItemEnchantmentType.BonusListID:
                                    {
                                        var bonusesEffect = Global.DB2Mgr.GetItemBonusList(gemEnchant.EffectArg[i]);
                                        if (bonusesEffect != null)
                                        {
                                            foreach (ItemBonusRecord itemBonus in bonusesEffect)
                                                if (itemBonus.BonusType == ItemBonusType.ItemLevel)

                                                    _bonusData.GemItemLevelBonus[slot] += (uint)itemBonus.Value[0];
                                        }
                                        break;
                                    }
                                case ItemEnchantmentType.BonusListCurve:
                                    {
                                        uint artifactrBonusListId = Global.DB2Mgr.GetItemBonusListForItemLevelDelta((short)Global.DB2Mgr.GetCurveValueAt((uint)Curves.ArtifactRelicItemLevelBonus, gemBaseItemLevel + gemBonus.ItemLevelBonus));
                                        if (artifactrBonusListId != 0)
                                        {
                                            var bonusesEffect = Global.DB2Mgr.GetItemBonusList(artifactrBonusListId);
                                            if (bonusesEffect != null)
                                                foreach (ItemBonusRecord itemBonus in bonusesEffect)
                                                    if (itemBonus.BonusType == ItemBonusType.ItemLevel)
                                                        _bonusData.GemItemLevelBonus[slot] += (uint)itemBonus.Value[0];
                                        }
                                        break;
                                    }
                                default:
                                    break;
                            }
                        }
                    }
                }
            }

            SocketedGem gemField = m_itemData.ModifyValue(m_itemData.Gems, slot);
            SetUpdateFieldValue(gemField.ModifyValue(gemField.ItemId), gem.ItemId);
            SetUpdateFieldValue(gemField.ModifyValue(gemField.Context), gem.Context);
            for (int i = 0; i < 16; ++i)
                SetUpdateFieldValue(ref gemField.ModifyValue(gemField.BonusListIDs, i), gem.BonusListIDs[i]);
        }

        public bool GemsFitSockets()
        {
            uint gemSlot = 0;
            foreach (SocketedGem gemData in m_itemData.Gems)
            {
                SocketColor SocketColor = GetTemplate().GetSocketColor(gemSlot);
                if (SocketColor == 0) // no socket slot
                    continue;

                SocketColor GemColor = 0;

                ItemTemplate gemProto = Global.ObjectMgr.GetItemTemplate(gemData.ItemId);
                if (gemProto != null)
                {
                    GemPropertiesRecord gemProperty = CliDB.GemPropertiesStorage.LookupByKey(gemProto.GetGemProperties());
                    if (gemProperty != null)
                        GemColor = gemProperty.Type;
                }

                if (!GemColor.HasAnyFlag(ItemConst.SocketColorToGemTypeMask[(int)SocketColor])) // bad gem color on this socket
                    return false;
            }
            return true;
        }

        public byte GetGemCountWithID(uint GemID)
        {
            var list = (List<SocketedGem>)m_itemData.Gems.GetEnumerator();
            return (byte)list.Count(gemData => gemData.ItemId == GemID);
        }

        public byte GetGemCountWithLimitCategory(uint limitCategory)
        {
            var list = (List<SocketedGem>)m_itemData.Gems;
            return (byte)list.Count(gemData =>
            {
                ItemTemplate gemProto = Global.ObjectMgr.GetItemTemplate(gemData.ItemId);
                if (gemProto == null)
                    return false;

                return gemProto.GetItemLimitCategory() == limitCategory;
            });
        }

        public bool IsLimitedToAnotherMapOrZone(uint cur_mapId, uint cur_zoneId)
        {
            ItemTemplate proto = GetTemplate();
            return proto != null && ((proto.GetMap() != 0 && proto.GetMap() != cur_mapId) ||
                ((proto.GetArea(0) != 0 && proto.GetArea(0) != cur_zoneId) && (proto.GetArea(1) != 0 && proto.GetArea(1) != cur_zoneId)));
        }

        public void SendUpdateSockets()
        {
            SocketGemsResult socketGems = new SocketGemsResult();
            socketGems.Item = GetGUID();

            GetOwner().SendPacket(socketGems);
        }

        public void SendTimeUpdate(Player owner)
        {
            uint duration = m_itemData.Expiration;
            if (duration == 0)
                return;

            ItemTimeUpdate itemTimeUpdate = new ItemTimeUpdate();
            itemTimeUpdate.ItemGuid = GetGUID();
            itemTimeUpdate.DurationLeft = duration;
            owner.SendPacket(itemTimeUpdate);
        }

        public static Item CreateItem(uint item, uint count, ItemContext context, Player player = null)
        {
            if (count < 1)
                return null;                                        //don't create item at zero count

            var pProto = Global.ObjectMgr.GetItemTemplate(item);
            if (pProto != null)
            {
                if (count > pProto.GetMaxStackSize())
                    count = pProto.GetMaxStackSize();

                Item pItem = Bag.NewItemOrBag(pProto);
                if (pItem.Create(Global.ObjectMgr.GetGenerator(HighGuid.Item).Generate(), item, context, player))
                {
                    pItem.SetCount(count);
                    return pItem;
                }
            }

            return null;
        }

        public Item CloneItem(uint count, Player player = null)
        {
            Item newItem = CreateItem(GetEntry(), count, GetContext(), player);
            if (newItem == null)
                return null;

            newItem.SetCreator(GetCreator());
            newItem.SetGiftCreator(GetGiftCreator());
            newItem.SetItemFlags((ItemFieldFlags)(m_itemData.DynamicFlags & ~(uint)(ItemFieldFlags.Refundable | ItemFieldFlags.BopTradeable)));
            newItem.SetExpiration(m_itemData.Expiration);
            // player CAN be NULL in which case we must not update random properties because that accesses player's item update queue
            if (player != null)
                newItem.SetItemRandomBonusList(m_randomBonusListId);
            return newItem;
        }

        public bool IsBindedNotWith(Player player)
        {
            // not binded item
            if (!IsSoulBound())
                return false;

            // own item
            if (GetOwnerGUID() == player.GetGUID())
                return false;

            if (HasItemFlag(ItemFieldFlags.BopTradeable))
                if (allowedGUIDs.Contains(player.GetGUID()))
                    return false;

            // BOA item case
            if (IsBoundAccountWide())
                return false;

            return true;
        }

        public override void BuildUpdate(Dictionary<Player, UpdateData> data)
        {
            Player owner = GetOwner();
            if (owner != null)
                BuildFieldsUpdate(owner, data);
            ClearUpdateMask(false);
        }

        public override UpdateFieldFlag GetUpdateFieldFlagsFor(Player target)
        {
            if (target.GetGUID() == GetOwnerGUID())
                return UpdateFieldFlag.Owner;

            return UpdateFieldFlag.None;
        }

        public override void BuildValuesCreate(WorldPacket data, Player target)
        {
            UpdateFieldFlag flags = GetUpdateFieldFlagsFor(target);
            WorldPacket buffer = new WorldPacket();

            m_objectData.WriteCreate(buffer, flags, this, target);
            m_itemData.WriteCreate(buffer, flags, this, target);

            data.WriteUInt32(buffer.GetSize() + 1);
            data.WriteUInt8((byte)flags);
            data.WriteBytes(buffer);
        }

        public override void BuildValuesUpdate(WorldPacket data, Player target)
        {
            UpdateFieldFlag flags = GetUpdateFieldFlagsFor(target);
            WorldPacket buffer = new WorldPacket();

            if (m_values.HasChanged(TypeId.Object))
                m_objectData.WriteUpdate(buffer, flags, this, target);

            if (m_values.HasChanged(TypeId.Item))
                m_itemData.WriteUpdate(buffer, flags, this, target);


            data.WriteUInt32(buffer.GetSize());
            data.WriteUInt32(m_values.GetChangedObjectTypeMask());
            data.WriteBytes(buffer);
        }

        public override void BuildValuesUpdateWithFlag(WorldPacket data, UpdateFieldFlag flags, Player target)
        {
            UpdateMask valuesMask = new UpdateMask(14);
            valuesMask.Set((int)TypeId.Item);

            WorldPacket buffer = new WorldPacket();
            UpdateMask mask = new UpdateMask(40);

            buffer.WriteUInt32(valuesMask.GetBlock(0));
            m_itemData.AppendAllowedFieldsMaskForFlag(mask, flags);
            m_itemData.WriteUpdate(buffer, mask, flags, this, target);

            data.WriteUInt32(buffer.GetSize());
            data.WriteBytes(buffer);
        }

        public override void ClearUpdateMask(bool remove)
        {
            m_values.ClearChangesMask(m_itemData);
            base.ClearUpdateMask(remove);
        }

        public override void AddToObjectUpdate()
        {
            Player owner = GetOwner();
            if (owner)
                owner.GetMap().AddUpdateObject(this);
        }

        public override void RemoveFromObjectUpdate()
        {
            Player owner = GetOwner();
            if (owner)
                owner.GetMap().RemoveUpdateObject(this);
        }

        public void SaveRefundDataToDB()
        {
            DeleteRefundDataFromDB();

            PreparedStatement stmt = DB.Characters.GetPreparedStatement(CharStatements.INS_ITEM_REFUND_INSTANCE);
            stmt.AddValue(0, GetGUID().GetCounter());
            stmt.AddValue(1, GetRefundRecipient().GetCounter());
            stmt.AddValue(2, GetPaidMoney());
            stmt.AddValue(3, (ushort)GetPaidExtendedCost());
            DB.Characters.Execute(stmt);
        }

        public void DeleteRefundDataFromDB(SQLTransaction trans = null)
        {
            PreparedStatement stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_REFUND_INSTANCE);
            stmt.AddValue(0, GetGUID().GetCounter());
            if (trans != null)
                trans.Append(stmt);
            else
                DB.Characters.Execute(stmt);
        }

        public void SetNotRefundable(Player owner, bool changestate = true, SQLTransaction trans = null, bool addToCollection = true)
        {
            if (!HasItemFlag(ItemFieldFlags.Refundable))
                return;

            ItemExpirePurchaseRefund itemExpirePurchaseRefund = new ItemExpirePurchaseRefund();
            itemExpirePurchaseRefund.ItemGUID = GetGUID();
            owner.SendPacket(itemExpirePurchaseRefund);

            RemoveItemFlag(ItemFieldFlags.Refundable);
            // Following is not applicable in the trading procedure
            if (changestate)
                SetState(ItemUpdateState.Changed, owner);

            SetRefundRecipient(ObjectGuid.Empty);
            SetPaidMoney(0);
            SetPaidExtendedCost(0);
            DeleteRefundDataFromDB(trans);

            owner.DeleteRefundReference(GetGUID());
            if (addToCollection)
                owner.GetSession().GetCollectionMgr().AddItemAppearance(this);
        }

        public void UpdatePlayedTime(Player owner)
        {
            // Get current played time
            uint current_playtime = m_itemData.CreatePlayedTime;
            // Calculate time elapsed since last played time update
            long curtime = Time.UnixTime;
            uint elapsed = (uint)(curtime - m_lastPlayedTimeUpdate);
            uint new_playtime = current_playtime + elapsed;
            // Check if the refund timer has expired yet
            if (new_playtime <= 2 * Time.Hour)
            {
                // No? Proceed.
                // Update the data field
                SetCreatePlayedTime(new_playtime);
                // Flag as changed to get saved to DB
                SetState(ItemUpdateState.Changed, owner);
                // Speaks for itself
                m_lastPlayedTimeUpdate = curtime;
                return;
            }
            // Yes
            SetNotRefundable(owner);
        }

        public uint GetPlayedTime()
        {
            long curtime = Time.UnixTime;
            uint elapsed = (uint)(curtime - m_lastPlayedTimeUpdate);
            return m_itemData.CreatePlayedTime + elapsed;
        }

        public bool IsRefundExpired()
        {
            return (GetPlayedTime() > 2 * Time.Hour);
        }

        public void SetSoulboundTradeable(List<ObjectGuid> allowedLooters)
        {
            AddItemFlag(ItemFieldFlags.BopTradeable);
            allowedGUIDs = allowedLooters;
        }

        public void ClearSoulboundTradeable(Player currentOwner)
        {
            RemoveItemFlag(ItemFieldFlags.BopTradeable);
            if (allowedGUIDs.Empty())
                return;

            currentOwner.GetSession().GetCollectionMgr().AddItemAppearance(this);
            allowedGUIDs.Clear();
            SetState(ItemUpdateState.Changed, currentOwner);
            PreparedStatement stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEM_BOP_TRADE);
            stmt.AddValue(0, GetGUID().GetCounter());
            DB.Characters.Execute(stmt);
        }

        public bool CheckSoulboundTradeExpire()
        {
            // called from owner's update - GetOwner() MUST be valid
            if (m_itemData.CreatePlayedTime + 2 * Time.Hour < GetOwner().GetTotalPlayedTime())
            {
                ClearSoulboundTradeable(GetOwner());
                return true; // remove from tradeable list
            }

            return false;
        }

        bool IsValidTransmogrificationTarget()
        {
            ItemTemplate proto = GetTemplate();
            if (proto == null)
                return false;

            if (proto.GetClass() != ItemClass.Armor &&
                proto.GetClass() != ItemClass.Weapon)
                return false;

            if (proto.GetClass() == ItemClass.Weapon && proto.GetSubClass() == (uint)ItemSubClassWeapon.FishingPole)
                return false;

            if (proto.GetFlags2().HasAnyFlag(ItemFlags2.NoAlterItemVisual))
                return false;

            if (!HasStats())
                return false;

            return true;
        }

        bool HasStats()
        {
            ItemTemplate proto = GetTemplate();
            Player owner = GetOwner();
            for (byte i = 0; i < ItemConst.MaxStats; ++i)
            {
                if ((owner ? GetItemStatValue(i, owner) : proto.GetItemStatAllocation(i)) != 0)
                    return true;
            }

            return false;
        }

        static bool HasStats(ItemInstance itemInstance, BonusData bonus)
        {
            for (byte i = 0; i < ItemConst.MaxStats; ++i)
            {
                if (bonus.ItemStatAllocation[i] != 0)
                    return true;
            }

            return false;
        }

        static ItemTransmogrificationWeaponCategory GetTransmogrificationWeaponCategory(ItemTemplate proto)
        {
            if (proto.GetClass() == ItemClass.Weapon)
            {
                switch ((ItemSubClassWeapon)proto.GetSubClass())
                {
                    case ItemSubClassWeapon.Axe2:
                    case ItemSubClassWeapon.Mace2:
                    case ItemSubClassWeapon.Sword2:
                    case ItemSubClassWeapon.Staff:
                    case ItemSubClassWeapon.Polearm:
                        return ItemTransmogrificationWeaponCategory.Melee2H;
                    case ItemSubClassWeapon.Bow:
                    case ItemSubClassWeapon.Gun:
                    case ItemSubClassWeapon.Crossbow:
                        return ItemTransmogrificationWeaponCategory.Ranged;
                    case ItemSubClassWeapon.Axe:
                    case ItemSubClassWeapon.Mace:
                    case ItemSubClassWeapon.Sword:
                    case ItemSubClassWeapon.Warglaives:
                        return ItemTransmogrificationWeaponCategory.AxeMaceSword1H;
                    case ItemSubClassWeapon.Dagger:
                        return ItemTransmogrificationWeaponCategory.Dagger;
                    case ItemSubClassWeapon.Fist:
                        return ItemTransmogrificationWeaponCategory.Fist;
                    default:
                        break;
                }
            }

            return ItemTransmogrificationWeaponCategory.Invalid;
        }

        public static int[] ItemTransmogrificationSlots =
        {
            -1,                                                     // INVTYPE_NON_EQUIP
            EquipmentSlot.Head,                                    // INVTYPE_HEAD
            -1,                                                    // INVTYPE_NECK
            EquipmentSlot.Shoulders,                               // INVTYPE_SHOULDERS
            EquipmentSlot.Shirt,                                    // INVTYPE_BODY
            EquipmentSlot.Chest,                                   // INVTYPE_CHEST
            EquipmentSlot.Waist,                                   // INVTYPE_WAIST
            EquipmentSlot.Legs,                                    // INVTYPE_LEGS
            EquipmentSlot.Feet,                                    // INVTYPE_FEET
            EquipmentSlot.Wrist,                                  // INVTYPE_WRISTS
            EquipmentSlot.Hands,                                   // INVTYPE_HANDS
            -1,                                                     // INVTYPE_FINGER
            -1,                                                     // INVTYPE_TRINKET
            -1,                                                     // INVTYPE_WEAPON
            EquipmentSlot.OffHand,                                 // INVTYPE_SHIELD
            EquipmentSlot.MainHand,                                // INVTYPE_RANGED
            EquipmentSlot.Cloak,                                    // INVTYPE_CLOAK
            EquipmentSlot.MainHand,                                 // INVTYPE_2HWEAPON
            -1,                                                     // INVTYPE_BAG
            EquipmentSlot.Tabard,                                  // INVTYPE_TABARD
            EquipmentSlot.Chest,                                   // INVTYPE_ROBE
            EquipmentSlot.MainHand,                                // INVTYPE_WEAPONMAINHAND
            EquipmentSlot.MainHand,                                 // INVTYPE_WEAPONOFFHAND
            EquipmentSlot.OffHand,                                 // INVTYPE_HOLDABLE
            -1,                                                     // INVTYPE_AMMO
            -1,                                                     // INVTYPE_THROWN
            EquipmentSlot.MainHand,                                // INVTYPE_RANGEDRIGHT
            -1,                                                     // INVTYPE_QUIVER
            -1                                                      // INVTYPE_RELIC
        };

        public static bool CanTransmogrifyItemWithItem(Item item, ItemModifiedAppearanceRecord itemModifiedAppearance)
        {
            ItemTemplate source = Global.ObjectMgr.GetItemTemplate(itemModifiedAppearance.ItemID); // source
            ItemTemplate target = item.GetTemplate(); // dest

            if (source == null || target == null)
                return false;

            if (itemModifiedAppearance == item.GetItemModifiedAppearance())
                return false;

            if (!item.IsValidTransmogrificationTarget())
                return false;

            if (source.GetClass() != target.GetClass())
                return false;

            if (source.GetInventoryType() == InventoryType.Bag ||
                source.GetInventoryType() == InventoryType.Relic ||
                source.GetInventoryType() == InventoryType.Finger ||
                source.GetInventoryType() == InventoryType.Trinket ||
                source.GetInventoryType() == InventoryType.Ammo ||
                source.GetInventoryType() == InventoryType.Quiver)
                return false;

            if (source.GetSubClass() != target.GetSubClass())
            {
                switch (source.GetClass())
                {
                    case ItemClass.Weapon:
                        if (GetTransmogrificationWeaponCategory(source) != GetTransmogrificationWeaponCategory(target))
                            return false;
                        break;
                    case ItemClass.Armor:
                        if ((ItemSubClassArmor)source.GetSubClass() != ItemSubClassArmor.Cosmetic)
                            return false;
                        if (source.GetInventoryType() != target.GetInventoryType())
                            if (ItemTransmogrificationSlots[(int)source.GetInventoryType()] != ItemTransmogrificationSlots[(int)target.GetInventoryType()])
                                return false;
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        uint GetBuyPrice(Player owner, out bool standardPrice)
        {
            return GetBuyPrice(GetTemplate(), (uint)GetQuality(), GetItemLevel(owner), out standardPrice);
        }

        static uint GetBuyPrice(ItemTemplate proto, uint quality, uint itemLevel, out bool standardPrice)
        {
            standardPrice = true;

            if (proto.GetFlags2().HasAnyFlag(ItemFlags2.OverrideGoldCost))
                return proto.GetBuyPrice();

            var qualityPrice = CliDB.ImportPriceQualityStorage.LookupByKey(quality + 1);
            if (qualityPrice == null)
                return 0;

            var basePrice = CliDB.ItemPriceBaseStorage.LookupByKey(proto.GetBaseItemLevel());
            if (basePrice == null)
                return 0;

            float qualityFactor = qualityPrice.Data;
            float baseFactor = 0.0f;

            var inventoryType = proto.GetInventoryType();

            if (inventoryType == InventoryType.Weapon ||
                inventoryType == InventoryType.Weapon2Hand ||
                inventoryType == InventoryType.WeaponMainhand ||
                inventoryType == InventoryType.WeaponOffhand ||
                inventoryType == InventoryType.Ranged ||
                inventoryType == InventoryType.Thrown ||
                inventoryType == InventoryType.RangedRight)
                baseFactor = basePrice.Weapon;
            else
                baseFactor = basePrice.Armor;

            if (inventoryType == InventoryType.Robe)
                inventoryType = InventoryType.Chest;

            if (proto.GetClass() == ItemClass.Gem && (ItemSubClassGem)proto.GetSubClass() == ItemSubClassGem.ArtifactRelic)
            {
                inventoryType = InventoryType.Weapon;
                baseFactor = basePrice.Weapon / 3.0f;
            }


            float typeFactor = 0.0f;
            sbyte weapType = -1;

            switch (inventoryType)
            {
                case InventoryType.Head:
                case InventoryType.Neck:
                case InventoryType.Shoulders:
                case InventoryType.Chest:
                case InventoryType.Waist:
                case InventoryType.Legs:
                case InventoryType.Feet:
                case InventoryType.Wrists:
                case InventoryType.Hands:
                case InventoryType.Finger:
                case InventoryType.Trinket:
                case InventoryType.Cloak:
                case InventoryType.Holdable:
                    {
                        var armorPrice = CliDB.ImportPriceArmorStorage.LookupByKey(inventoryType);
                        if (armorPrice == null)
                            return 0;

                        switch ((ItemSubClassArmor)proto.GetSubClass())
                        {
                            case ItemSubClassArmor.Miscellaneous:
                            case ItemSubClassArmor.Cloth:
                                typeFactor = armorPrice.ClothModifier;
                                break;
                            case ItemSubClassArmor.Leather:
                                typeFactor = armorPrice.LeatherModifier;
                                break;
                            case ItemSubClassArmor.Mail:
                                typeFactor = armorPrice.ChainModifier;
                                break;
                            case ItemSubClassArmor.Plate:
                                typeFactor = armorPrice.PlateModifier;
                                break;
                            default:
                                typeFactor = 1.0f;
                                break;
                        }

                        break;
                    }
                case InventoryType.Shield:
                    {
                        var shieldPrice = CliDB.ImportPriceShieldStorage.LookupByKey(2); // it only has two rows, it's unclear which is the one used
                        if (shieldPrice == null)
                            return 0;

                        typeFactor = shieldPrice.Data;
                        break;
                    }
                case InventoryType.WeaponMainhand:
                    weapType = 0;
                    break;
                case InventoryType.WeaponOffhand:
                    weapType = 1;
                    break;
                case InventoryType.Weapon:
                    weapType = 2;
                    break;
                case InventoryType.Weapon2Hand:
                    weapType = 3;
                    break;
                case InventoryType.Ranged:
                case InventoryType.RangedRight:
                case InventoryType.Relic:
                    weapType = 4;
                    break;
                default:
                    return proto.GetBuyPrice();
            }

            if (weapType != -1)
            {
                var weaponPrice = CliDB.ImportPriceWeaponStorage.LookupByKey(weapType + 1);
                if (weaponPrice == null)
                    return 0;

                typeFactor = weaponPrice.Data;
            }

            standardPrice = false;
            return (uint)(proto.GetPriceVariance() * typeFactor * baseFactor * qualityFactor * proto.GetPriceRandomValue());
        }

        public uint GetSellPrice(Player owner)
        {
            return GetSellPrice(GetTemplate(), (uint)GetQuality(), GetItemLevel(owner));
        }

        public static uint GetSellPrice(ItemTemplate proto, uint quality, uint itemLevel)
        {
            if (proto.GetFlags2().HasAnyFlag(ItemFlags2.OverrideGoldCost))
                return proto.GetSellPrice();

            bool standardPrice;
            uint cost = GetBuyPrice(proto, quality, itemLevel, out standardPrice);

            if (standardPrice)
            {
                ItemClassRecord classEntry = Global.DB2Mgr.GetItemClassByOldEnum(proto.GetClass());
                if (classEntry != null)
                {
                    uint buyCount = Math.Max(proto.GetBuyCount(), 1u);
                    return (uint)(cost * classEntry.PriceModifier / buyCount);
                }

                return 0;
            }
            else
                return proto.GetSellPrice();
        }

        public void ItemContainerSaveLootToDB()
        {
            // Saves the money and item loot associated with an openable item to the DB
            if (loot.IsLooted()) // no money and no loot
                return;

            SQLTransaction trans = new SQLTransaction();

            loot.containerID = GetGUID(); // Save this for when a LootItem is removed

            // Save money
            if (loot.gold > 0)
            {
                PreparedStatement stmt_money = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEMCONTAINER_MONEY);
                stmt_money.AddValue(0, loot.containerID.GetCounter());
                trans.Append(stmt_money);

                stmt_money = DB.Characters.GetPreparedStatement(CharStatements.INS_ITEMCONTAINER_MONEY);
                stmt_money.AddValue(0, loot.containerID.GetCounter());
                stmt_money.AddValue(1, loot.gold);
                trans.Append(stmt_money);
            }

            // Save items
            if (!loot.IsLooted())
            {
                PreparedStatement stmt_items = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEMCONTAINER_ITEMS);
                stmt_items.AddValue(0, loot.containerID.GetCounter());
                trans.Append(stmt_items);

                // Now insert the items
                foreach (var _li in loot.items)
                {
                    // When an item is looted, it doesn't get removed from the items collection
                    //  but we don't want to resave it.
                    if (!_li.canSave)
                        continue;

                    Player guid = GetOwner();
                    if (!_li.AllowedForPlayer(guid))
                        continue;

                    stmt_items = DB.Characters.GetPreparedStatement(CharStatements.INS_ITEMCONTAINER_ITEMS);

                    // container_id, item_id, item_count, follow_rules, ffa, blocked, counted, under_threshold, needs_quest, rnd_prop, context, bonus_list_ids
                    stmt_items.AddValue(0, loot.containerID.GetCounter());
                    stmt_items.AddValue(1, _li.itemid);
                    stmt_items.AddValue(2, _li.count);
                    stmt_items.AddValue(3, _li.follow_loot_rules);
                    stmt_items.AddValue(4, _li.freeforall);
                    stmt_items.AddValue(5, _li.is_blocked);
                    stmt_items.AddValue(6, _li.is_counted);
                    stmt_items.AddValue(7, _li.is_underthreshold);
                    stmt_items.AddValue(8, _li.needs_quest);
                    stmt_items.AddValue(9, _li.randomBonusListId);
                    stmt_items.AddValue(10, _li.context);

                    string bonusListIDs = "";
                    foreach (int bonusListID in _li.BonusListIDs)
                        bonusListIDs += bonusListID + ' ';

                    stmt_items.AddValue(11, bonusListIDs);
                    trans.Append(stmt_items);
                }
            }
            DB.Characters.CommitTransaction(trans);
        }

        public bool ItemContainerLoadLootFromDB()
        {
            // Loads the money and item loot associated with an openable item from the DB
            // Default. If there are no records for this item then it will be rolled for in Player.SendLoot()
            m_lootGenerated = false;

            // Save this for later use
            loot.containerID = GetGUID();

            // First, see if there was any money loot. This gets added directly to the container.
            PreparedStatement stmt = DB.Characters.GetPreparedStatement(CharStatements.SEL_ITEMCONTAINER_MONEY);
            stmt.AddValue(0, loot.containerID.GetCounter());
            SQLResult money_result = DB.Characters.Query(stmt);

            if (!money_result.IsEmpty())
            {
                loot.gold = money_result.Read<uint>(0);
            }

            // Next, load any items that were saved
            stmt = DB.Characters.GetPreparedStatement(CharStatements.SEL_ITEMCONTAINER_ITEMS);
            stmt.AddValue(0, loot.containerID.GetCounter());
            SQLResult item_result = DB.Characters.Query(stmt);

            if (!item_result.IsEmpty())
            {
                // Get a LootTemplate for the container item. This is where
                //  the saved loot was originally rolled from, we will copy conditions from it
                LootTemplate lt = LootStorage.Items.GetLootFor(GetEntry());
                if (lt != null)
                {
                    do
                    {
                        // Create an empty LootItem
                        LootItem loot_item = new LootItem();

                        // item_id, itm_count, follow_rules, ffa, blocked, counted, under_threshold, needs_quest, rnd_prop, context, bonus_list_ids
                        loot_item.itemid = item_result.Read<uint>(0);
                        loot_item.count = item_result.Read<byte>(1);
                        loot_item.follow_loot_rules = item_result.Read<bool>(2);
                        loot_item.freeforall = item_result.Read<bool>(3);
                        loot_item.is_blocked = item_result.Read<bool>(4);
                        loot_item.is_counted = item_result.Read<bool>(5);
                        loot_item.canSave = true;
                        loot_item.is_underthreshold = item_result.Read<bool>(6);
                        loot_item.needs_quest = item_result.Read<bool>(7);
                        loot_item.randomBonusListId = item_result.Read<uint>(8);
                        loot_item.context = (ItemContext)item_result.Read<byte>(9);

                        StringArray bonusLists = new StringArray(item_result.Read<string>(10), ' ');
                        if (!bonusLists.IsEmpty())
                        {
                            foreach (string line in bonusLists)
                            {
                                if (uint.TryParse(line, out uint id))
                                    loot_item.BonusListIDs.Add(id);
                            }
                        }

                        // Copy the extra loot conditions from the item in the loot template
                        lt.CopyConditions(loot_item);

                        // If container item is in a bag, add that player as an allowed looter
                        if (GetBagSlot() != 0)
                            loot_item.AddAllowedLooter(GetOwner());

                        // Finally add the LootItem to the container
                        loot.items.Add(loot_item);

                        // Increment unlooted count
                        loot.unlootedCount++;
                    }
                    while (item_result.NextRow());
                }
            }

            // Mark the item if it has loot so it won't be generated again on open
            m_lootGenerated = !loot.IsLooted();

            return m_lootGenerated;
        }

        void ItemContainerDeleteLootItemsFromDB()
        {
            // Deletes items associated with an openable item from the DB
            PreparedStatement stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEMCONTAINER_ITEMS);
            stmt.AddValue(0, GetGUID().GetCounter());
            DB.Characters.Execute(stmt);
        }

        void ItemContainerDeleteLootItemFromDB(uint itemID)
        {
            // Deletes a single item associated with an openable item from the DB
            PreparedStatement stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEMCONTAINER_ITEM);
            stmt.AddValue(0, GetGUID().GetCounter());
            stmt.AddValue(1, itemID);
            DB.Characters.Execute(stmt);
        }

        void ItemContainerDeleteLootMoneyFromDB()
        {
            // Deletes the money loot associated with an openable item from the DB
            PreparedStatement stmt = DB.Characters.GetPreparedStatement(CharStatements.DEL_ITEMCONTAINER_MONEY);
            stmt.AddValue(0, GetGUID().GetCounter());
            DB.Characters.Execute(stmt);
        }

        public void ItemContainerDeleteLootMoneyAndLootItemsFromDB()
        {
            // Deletes money and items associated with an openable item from the DB
            ItemContainerDeleteLootMoneyFromDB();
            ItemContainerDeleteLootItemsFromDB();
        }

        public uint GetItemLevel(Player owner)
        {
            ItemTemplate itemTemplate = GetTemplate();
            uint minItemLevel = owner.m_unitData.MinItemLevel;
            uint minItemLevelCutoff = owner.m_unitData.MinItemLevelCutoff;
            uint maxItemLevel = itemTemplate.GetFlags3().HasAnyFlag(ItemFlags3.IgnoreItemLevelCapInPvp) ? 0u : owner.m_unitData.MaxItemLevel;
            bool pvpBonus = owner.IsUsingPvpItemLevels();

            uint azeriteLevel = 0;
            AzeriteItem azeriteItem = ToAzeriteItem();
            if (azeriteItem != null)
                azeriteLevel = azeriteItem.GetEffectiveLevel();

            return GetItemLevel(itemTemplate, _bonusData, owner.GetLevel(), GetModifier(ItemModifier.TimewalkerLevel),
                minItemLevel, minItemLevelCutoff, maxItemLevel, pvpBonus, azeriteLevel);
        }

        public static uint GetItemLevel(ItemTemplate itemTemplate, BonusData bonusData, uint level, uint fixedLevel, uint minItemLevel, uint minItemLevelCutoff, uint maxItemLevel, bool pvpBonus, uint azeriteLevel)
        {
            if (itemTemplate == null)
                return 1;

            uint itemLevel = itemTemplate.GetBaseItemLevel();
            AzeriteLevelInfoRecord azeriteLevelInfo = CliDB.AzeriteLevelInfoStorage.LookupByKey(azeriteLevel);
            if (azeriteLevelInfo != null)
                itemLevel = azeriteLevelInfo.ItemLevel;

            ScalingStatDistributionRecord ssd = CliDB.ScalingStatDistributionStorage.LookupByKey(bonusData.ScalingStatDistribution);
            if (ssd != null)
            {
                if (fixedLevel != 0)
                    level = fixedLevel;
                else
                    level = (uint)Math.Min(Math.Max(level, ssd.MinLevel), ssd.MaxLevel);

                ContentTuningRecord contentTuning = CliDB.ContentTuningStorage.LookupByKey(bonusData.ContentTuningId);
                if (contentTuning != null)
                    if ((Convert.ToBoolean(contentTuning.Flags & 2) || contentTuning.MinLevel != 0 || contentTuning.MaxLevel != 0) && !Convert.ToBoolean(contentTuning.Flags & 4))
                        level = (uint)Math.Min(Math.Max(level, contentTuning.MinLevel), contentTuning.MaxLevel);

                uint heirloomIlvl = (uint)Global.DB2Mgr.GetCurveValueAt(ssd.PlayerLevelToItemLevelCurveID, level);
                if (heirloomIlvl != 0)
                    itemLevel = heirloomIlvl;
            }

            itemLevel += (uint)bonusData.ItemLevelBonus;

            for (uint i = 0; i < ItemConst.MaxGemSockets; ++i)
                itemLevel += bonusData.GemItemLevelBonus[i];

            uint itemLevelBeforeUpgrades = itemLevel;

            if (pvpBonus)
                itemLevel += Global.DB2Mgr.GetPvpItemLevelBonus(itemTemplate.GetId());

            if (itemTemplate.GetInventoryType() != InventoryType.NonEquip)
            {
                if (minItemLevel != 0 && (minItemLevelCutoff == 0 || itemLevelBeforeUpgrades >= minItemLevelCutoff) && itemLevel < minItemLevel)
                    itemLevel = minItemLevel;

                if (maxItemLevel != 0 && itemLevel > maxItemLevel)
                    itemLevel = maxItemLevel;
            }

            return Math.Min(Math.Max(itemLevel, 1), 1300);
        }

        public int GetItemStatValue(uint index, Player owner)
        {
            Cypher.Assert(index < ItemConst.MaxStats);
            uint itemLevel = GetItemLevel(owner);
            uint randomPropPoints = ItemEnchantmentManager.GetRandomPropertyPoints(itemLevel, GetQuality(), GetTemplate().GetInventoryType(), GetTemplate().GetSubClass());
            if (randomPropPoints != 0)
            {
                float statValue = (_bonusData.ItemStatAllocation[index] * randomPropPoints) * 0.0001f;
                GtItemSocketCostPerLevelRecord gtCost = CliDB.ItemSocketCostPerLevelGameTable.GetRow(itemLevel);
                if (gtCost != null)
                    statValue -= (_bonusData.ItemStatSocketCostMultiplier[index] * gtCost.SocketCost);

                return (int)(Math.Floor(statValue + 0.5f));
            }

            return 0;
        }

        public ItemDisenchantLootRecord GetDisenchantLoot(Player owner)
        {
            if (!_bonusData.CanDisenchant)
                return null;

            return GetDisenchantLoot(GetTemplate(), (uint)GetQuality(), GetItemLevel(owner));
        }

        public static ItemDisenchantLootRecord GetDisenchantLoot(ItemTemplate itemTemplate, uint quality, uint itemLevel)
        {
            if (itemTemplate.GetFlags().HasAnyFlag(ItemFlags.Conjured | ItemFlags.NoDisenchant) || itemTemplate.GetBonding() == ItemBondingType.Quest)
                return null;

            if (itemTemplate.GetArea(0) != 0 || itemTemplate.GetArea(1) != 0 || itemTemplate.GetMap() != 0 || itemTemplate.GetMaxStackSize() > 1)
                return null;

            if (GetSellPrice(itemTemplate, quality, itemLevel) == 0 && !Global.DB2Mgr.HasItemCurrencyCost(itemTemplate.GetId()))
                return null;

            byte itemClass = (byte)itemTemplate.GetClass();
            uint itemSubClass = itemTemplate.GetSubClass();
            byte expansion = itemTemplate.GetRequiredExpansion();
            foreach (ItemDisenchantLootRecord disenchant in CliDB.ItemDisenchantLootStorage.Values)
            {
                if (disenchant.Class != itemClass)
                    continue;

                if (disenchant.Subclass >= 0 && itemSubClass != 0)
                    continue;

                if (disenchant.Quality != quality)
                    continue;

                if (disenchant.MinLevel > itemLevel || disenchant.MaxLevel < itemLevel)
                    continue;

                if (disenchant.ExpansionID != -2 && disenchant.ExpansionID != expansion)
                    continue;

                return disenchant;
            }

            return null;
        }

        public uint GetDisplayId(Player owner)
        {
            ItemModifier transmogModifier = ItemModifier.TransmogAppearanceAllSpecs;
            if ((m_itemData.ModifiersMask & ItemConst.AppearanceModifierMaskSpecSpecific) != 0)
                transmogModifier = ItemConst.AppearanceModifierSlotBySpec[owner.GetActiveTalentGroup()];

            ItemModifiedAppearanceRecord transmog = CliDB.ItemModifiedAppearanceStorage.LookupByKey(GetModifier(transmogModifier));
            if (transmog != null)
            {
                ItemAppearanceRecord itemAppearance = CliDB.ItemAppearanceStorage.LookupByKey(transmog.ItemAppearanceID);
                if (itemAppearance != null)
                    return itemAppearance.ItemDisplayInfoID;
            }

            return Global.DB2Mgr.GetItemDisplayId(GetEntry(), GetAppearanceModId());
        }

        public ItemModifiedAppearanceRecord GetItemModifiedAppearance()
        {
            return Global.DB2Mgr.GetItemModifiedAppearance(GetEntry(), _bonusData.AppearanceModID);
        }

        public uint GetModifier(ItemModifier modifier)
        {
            if ((m_itemData.ModifiersMask & (1 << (int)modifier)) == 0)
                return 0;

            int valueIndex = 0;
            uint mask = m_itemData.ModifiersMask;
            for (int i = 0; i < (int)modifier; ++i)
                if ((mask & (1 << i)) != 0)
                    ++valueIndex;

            return m_itemData.Modifiers[valueIndex];
        }

        public void SetModifier(ItemModifier modifier, uint value)
        {
            int valueIndex = 0;
            uint mask = m_itemData.ModifiersMask;
            for (int i = 0; i < (int)modifier; ++i)
                if ((mask & (1 << i)) != 0)
                    ++valueIndex;

            if (value != 0)
            {
                if ((mask & (1 << (int)modifier)) != 0)
                    return;

                SetUpdateFieldFlagValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.ModifiersMask), 1u << (int)modifier);
                InsertDynamicUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.Modifiers), valueIndex, value);
            }
            else
            {
                if ((mask & (1 << (int)modifier)) == 0)
                    return;

                RemoveUpdateFieldFlagValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.ModifiersMask), 1u << (int)modifier);
                RemoveDynamicUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.Modifiers), valueIndex);
            }
        }

        public uint GetVisibleEntry(Player owner)
        {
            ItemModifier transmogModifier = ItemModifier.TransmogAppearanceAllSpecs;
            if ((m_itemData.ModifiersMask & ItemConst.AppearanceModifierMaskSpecSpecific) != 0)
                transmogModifier = ItemConst.AppearanceModifierSlotBySpec[owner.GetActiveTalentGroup()];

            ItemModifiedAppearanceRecord transmog = CliDB.ItemModifiedAppearanceStorage.LookupByKey(GetModifier(transmogModifier));
            if (transmog != null)
                return transmog.ItemID;

            return GetEntry();
        }

        public ushort GetVisibleAppearanceModId(Player owner)
        {
            ItemModifier transmogModifier = ItemModifier.TransmogAppearanceAllSpecs;
            if ((m_itemData.ModifiersMask & ItemConst.AppearanceModifierMaskSpecSpecific) != 0)
                transmogModifier = ItemConst.AppearanceModifierSlotBySpec[owner.GetActiveTalentGroup()];

            ItemModifiedAppearanceRecord transmog = CliDB.ItemModifiedAppearanceStorage.LookupByKey(GetModifier(transmogModifier));
            if (transmog != null)
                return transmog.ItemAppearanceModifierID;

            return (ushort)GetAppearanceModId();
        }

        public uint GetVisibleEnchantmentId(Player owner)
        {
            ItemModifier illusionModifier = ItemModifier.EnchantIllusionAllSpecs;
            if ((m_itemData.ModifiersMask & ItemConst.IllusionModifierMaskSpecSpecific) != 0)
                illusionModifier = ItemConst.IllusionModifierSlotBySpec[owner.GetActiveTalentGroup()];

            uint enchantIllusion = GetModifier(illusionModifier);
            if (enchantIllusion != 0)
                return enchantIllusion;

            return (uint)GetEnchantmentId(EnchantmentSlot.Perm);
        }

        public ushort GetVisibleItemVisual(Player owner)
        {
            SpellItemEnchantmentRecord enchant = CliDB.SpellItemEnchantmentStorage.LookupByKey(GetVisibleEnchantmentId(owner));
            if (enchant != null)
                return enchant.ItemVisual;

            return 0;
        }

        public void AddBonuses(uint bonusListID)
        {
            var bonusListIDs = (List<uint>)m_itemData.BonusListIDs;
            if (bonusListIDs.Contains(bonusListID))
                return;

            var bonuses = Global.DB2Mgr.GetItemBonusList(bonusListID);
            if (bonuses != null)
            {
                bonusListIDs.Add(bonusListID);
                SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.BonusListIDs), bonusListIDs);
                foreach (ItemBonusRecord bonus in bonuses)
                    _bonusData.AddBonus(bonus.BonusType, bonus.Value);

                SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.ItemAppearanceModID), (byte)_bonusData.AppearanceModID);
            }
        }

        public void SetBonuses(List<uint> bonusListIDs)
        {
            if (bonusListIDs == null)
                bonusListIDs = new List<uint>();

            SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.BonusListIDs), bonusListIDs);

            foreach (uint bonusListID in (List<uint>)m_itemData.BonusListIDs)
                _bonusData.AddBonusList(bonusListID);

            SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.ItemAppearanceModID), (byte)_bonusData.AppearanceModID);
        }

        public void ClearBonuses()
        {
            SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.BonusListIDs), new List<uint>());
            _bonusData = new BonusData(GetTemplate());
            SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.ItemAppearanceModID), (byte)_bonusData.AppearanceModID);
        }

        public bool IsArtifactDisabled()
        {
            ArtifactRecord artifact = CliDB.ArtifactStorage.LookupByKey(GetTemplate().GetArtifactID());
            if (artifact != null)
                return artifact.ArtifactCategoryID != 2; // fishing artifact

            return true;
        }

        public ArtifactPower GetArtifactPower(uint artifactPowerId)
        {
            var index = m_artifactPowerIdToIndex.LookupByKey(artifactPowerId);
            if (index != 0)
                return m_itemData.ArtifactPowers[index];

            return null;
        }

        void AddArtifactPower(ArtifactPowerData artifactPower)
        {
            int index = m_artifactPowerIdToIndex.Count;
            m_artifactPowerIdToIndex[artifactPower.ArtifactPowerId] = (ushort)index;

            ArtifactPower powerField = new ArtifactPower();
            powerField.ArtifactPowerId = (ushort)artifactPower.ArtifactPowerId;
            powerField.PurchasedRank = artifactPower.PurchasedRank;
            powerField.CurrentRankWithBonus = artifactPower.CurrentRankWithBonus;

            AddDynamicUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.ArtifactPowers), powerField);
        }

        public void SetArtifactPower(ushort artifactPowerId, byte purchasedRank, byte currentRankWithBonus)
        {
            var foundIndex = m_artifactPowerIdToIndex.LookupByKey(artifactPowerId);
            if (foundIndex != 0)
            {
                ArtifactPower artifactPower = m_itemData.ModifyValue(m_itemData.ArtifactPowers, foundIndex);
                SetUpdateFieldValue(ref artifactPower.PurchasedRank, purchasedRank);
                SetUpdateFieldValue(ref artifactPower.CurrentRankWithBonus, currentRankWithBonus);
            }
        }

        public void InitArtifactPowers(byte artifactId, byte artifactTier)
        {
            foreach (ArtifactPowerRecord artifactPower in Global.DB2Mgr.GetArtifactPowers(artifactId))
            {
                if (artifactPower.Tier != artifactTier)
                    continue;

                if (m_artifactPowerIdToIndex.ContainsKey(artifactPower.Id))
                    continue;

                ArtifactPowerData powerData = new ArtifactPowerData();
                powerData.ArtifactPowerId = artifactPower.Id;
                powerData.PurchasedRank = 0;
                powerData.CurrentRankWithBonus = (byte)((artifactPower.Flags & ArtifactPowerFlag.First) == ArtifactPowerFlag.First ? 1 : 0);
                AddArtifactPower(powerData);
            }
        }

        public uint GetTotalPurchasedArtifactPowers()
        {
            uint purchasedRanks = 0;
            foreach (ArtifactPower power in m_itemData.ArtifactPowers)
                purchasedRanks += power.PurchasedRank;

            return purchasedRanks;
        }

        void ApplyArtifactPowerEnchantmentBonuses(EnchantmentSlot slot, uint enchantId, bool apply, Player owner)
        {
            SpellItemEnchantmentRecord enchant = CliDB.SpellItemEnchantmentStorage.LookupByKey(enchantId);
            if (enchant != null)
            {
                for (uint i = 0; i < ItemConst.MaxItemEnchantmentEffects; ++i)
                {
                    switch (enchant.Effect[i])
                    {
                        case ItemEnchantmentType.ArtifactPowerBonusRankByType:
                            {
                                for (int artifactPowerIndex = 0; artifactPowerIndex < m_itemData.ArtifactPowers.Size(); ++artifactPowerIndex)
                                {
                                    ArtifactPower artifactPower = m_itemData.ArtifactPowers[artifactPowerIndex];
                                    if (CliDB.ArtifactPowerStorage.LookupByKey(artifactPower.ArtifactPowerId).Label == enchant.EffectArg[i])
                                    {
                                        byte newRank = artifactPower.CurrentRankWithBonus;
                                        if (apply)
                                            newRank += (byte)enchant.EffectPointsMin[i];
                                        else
                                            newRank -= (byte)enchant.EffectPointsMin[i];

                                        artifactPower = m_itemData.ModifyValue(m_itemData.ArtifactPowers, artifactPowerIndex);
                                        SetUpdateFieldValue(ref artifactPower.CurrentRankWithBonus, newRank);

                                        if (IsEquipped())
                                        {
                                            ArtifactPowerRankRecord artifactPowerRank = Global.DB2Mgr.GetArtifactPowerRank(artifactPower.ArtifactPowerId, (byte)(newRank != 0 ? newRank - 1 : 0));
                                            if (artifactPowerRank != null)
                                                owner.ApplyArtifactPowerRank(this, artifactPowerRank, newRank != 0);
                                        }
                                    }
                                }
                            }
                            break;
                        case ItemEnchantmentType.ArtifactPowerBonusRankByID:
                            {
                                var indexItr = m_artifactPowerIdToIndex.LookupByKey(enchant.EffectArg[i]);
                                ushort index;
                                if (indexItr != 0)
                                    index = indexItr;

                                ushort artifactPowerIndex = m_artifactPowerIdToIndex.LookupByKey(enchant.EffectArg[i]);
                                if (artifactPowerIndex != 0)
                                {
                                    byte newRank = m_itemData.ArtifactPowers[artifactPowerIndex].CurrentRankWithBonus;
                                    if (apply)
                                        newRank += (byte)enchant.EffectPointsMin[i];
                                    else
                                        newRank -= (byte)enchant.EffectPointsMin[i];

                                    ArtifactPower artifactPower = m_itemData.ModifyValue(m_itemData.ArtifactPowers, artifactPowerIndex);
                                    SetUpdateFieldValue(ref artifactPower.CurrentRankWithBonus, newRank);

                                    if (IsEquipped())
                                    {
                                        ArtifactPowerRankRecord artifactPowerRank = Global.DB2Mgr.GetArtifactPowerRank(m_itemData.ArtifactPowers[artifactPowerIndex].ArtifactPowerId, (byte)(newRank != 0 ? newRank - 1 : 0));
                                        if (artifactPowerRank != null)
                                            owner.ApplyArtifactPowerRank(this, artifactPowerRank, newRank != 0);
                                    }
                                }
                            }
                            break;
                        case ItemEnchantmentType.ArtifactPowerBonusRankPicker:
                            if (slot >= EnchantmentSlot.Sock1 && slot <= EnchantmentSlot.Sock3 && _bonusData.GemRelicType[slot - EnchantmentSlot.Sock1] != -1)
                            {
                                ArtifactPowerPickerRecord artifactPowerPicker = CliDB.ArtifactPowerPickerStorage.LookupByKey(enchant.EffectArg[i]);
                                if (artifactPowerPicker != null)
                                {
                                    PlayerConditionRecord playerCondition = CliDB.PlayerConditionStorage.LookupByKey(artifactPowerPicker.PlayerConditionID);
                                    if (playerCondition == null || ConditionManager.IsPlayerMeetingCondition(owner, playerCondition))
                                    {
                                        for (int artifactPowerIndex = 0; artifactPowerIndex < m_itemData.ArtifactPowers.Size(); ++artifactPowerIndex)
                                        {
                                            ArtifactPower artifactPower = m_itemData.ArtifactPowers[artifactPowerIndex];
                                            if (CliDB.ArtifactPowerStorage.LookupByKey(artifactPower.ArtifactPowerId).Label == _bonusData.GemRelicType[slot - EnchantmentSlot.Sock1])
                                            {
                                                byte newRank = artifactPower.CurrentRankWithBonus;
                                                if (apply)
                                                    newRank += (byte)enchant.EffectPointsMin[i];
                                                else
                                                    newRank -= (byte)enchant.EffectPointsMin[i];

                                                artifactPower = m_itemData.ModifyValue(m_itemData.ArtifactPowers, artifactPowerIndex);
                                                SetUpdateFieldValue(ref artifactPower.CurrentRankWithBonus, newRank);

                                                if (IsEquipped())
                                                {
                                                    ArtifactPowerRankRecord artifactPowerRank = Global.DB2Mgr.GetArtifactPowerRank(artifactPower.ArtifactPowerId, (byte)(newRank != 0 ? newRank - 1 : 0));
                                                    if (artifactPowerRank != null)
                                                        owner.ApplyArtifactPowerRank(this, artifactPowerRank, newRank != 0);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        public void CopyArtifactDataFromParent(Item parent)
        {
            Array.Copy(parent.GetBonus().GemItemLevelBonus, _bonusData.GemItemLevelBonus, _bonusData.GemItemLevelBonus.Length);
            SetModifier(ItemModifier.ArtifactAppearanceId, parent.GetModifier(ItemModifier.ArtifactAppearanceId));
            SetAppearanceModId(parent.GetAppearanceModId());
        }

        public void SetArtifactXP(ulong xp) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.ArtifactXP), xp); }
        public void GiveArtifactXp(ulong amount, Item sourceItem, ArtifactCategory artifactCategoryId)
        {
            Player owner = GetOwner();
            if (!owner)
                return;

            if (artifactCategoryId != 0)
            {
                uint artifactKnowledgeLevel = 1;
                if (sourceItem != null && sourceItem.GetModifier(ItemModifier.ArtifactKnowledgeLevel) != 0)
                    artifactKnowledgeLevel = sourceItem.GetModifier(ItemModifier.ArtifactKnowledgeLevel);

                GtArtifactKnowledgeMultiplierRecord artifactKnowledge = CliDB.ArtifactKnowledgeMultiplierGameTable.GetRow(artifactKnowledgeLevel);
                if (artifactKnowledge != null)
                    amount = (ulong)(amount * artifactKnowledge.Multiplier);

                if (amount >= 5000)
                    amount = 50 * (amount / 50);
                else if (amount >= 1000)
                    amount = 25 * (amount / 25);
                else if (amount >= 50)
                    amount = 5 * (amount / 5);
            }

            SetArtifactXP(m_itemData.ArtifactXP + amount);

            ArtifactXpGain artifactXpGain = new ArtifactXpGain();
            artifactXpGain.ArtifactGUID = GetGUID();
            artifactXpGain.Amount = amount;
            owner.SendPacket(artifactXpGain);

            SetState(ItemUpdateState.Changed, owner);
        }

        public ItemContext GetContext() { return (ItemContext)(int)m_itemData.Context;    }
        public void SetContext(ItemContext context) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.Context), (int)context); }

        public void SetPetitionId(uint petitionId)
        {
            SetUpdateFieldValue(m_itemData.ModifyValue(m_itemData.Enchantment, 0).ModifyValue((ItemEnchantment itemEnchantment) => itemEnchantment.ID), petitionId);
        }
        public void SetPetitionNumSignatures(uint signatures)
        {
            SetUpdateFieldValue(m_itemData.ModifyValue(m_itemData.Enchantment, 0).ModifyValue((ItemEnchantment itemEnchantment) => itemEnchantment.Duration), signatures);
        }

        public void SetFixedLevel(uint level)
        {
            if (!_bonusData.HasFixedLevel || GetModifier(ItemModifier.TimewalkerLevel) != 0)
                return;

            ScalingStatDistributionRecord ssd = CliDB.ScalingStatDistributionStorage.LookupByKey(_bonusData.ScalingStatDistribution);
            if (ssd != null)
            {
                level = (uint)Math.Min(Math.Max(level, ssd.MinLevel), ssd.MaxLevel);

                ContentTuningRecord contentTuning = CliDB.ContentTuningStorage.LookupByKey(_bonusData.ContentTuningId);
                if (contentTuning != null)
                    if ((contentTuning.Flags.HasAnyFlag(2) || contentTuning.MinLevel != 0 || contentTuning.MaxLevel != 0) && !contentTuning.Flags.HasAnyFlag(4))
                        level = (uint)Math.Min(Math.Max(level, contentTuning.MinLevel), contentTuning.MaxLevel);

                SetModifier(ItemModifier.TimewalkerLevel, level);
            }
        }

        public int GetRequiredLevel()
        {
            if (_bonusData.RequiredLevelOverride != 0)
                return _bonusData.RequiredLevelOverride;
            else if (_bonusData.HasFixedLevel)
                return (int)GetModifier(ItemModifier.TimewalkerLevel);
            else
                return _bonusData.RequiredLevel;
        }

        public static Item NewItemOrBag(ItemTemplate proto)
        {
            if (proto.GetInventoryType() == InventoryType.Bag)
                return new Bag();

            if (Global.DB2Mgr.IsAzeriteItem(proto.GetId()))
                return new AzeriteItem();

            if (Global.DB2Mgr.GetAzeriteEmpoweredItem(proto.GetId()) != null)
                return new AzeriteEmpoweredItem();

            return new Item();
        }

        public static void AddItemsSetItem(Player player, Item item)
        {
            ItemTemplate proto = item.GetTemplate();
            uint setid = proto.GetItemSet();

            ItemSetRecord set = CliDB.ItemSetStorage.LookupByKey(setid);
            if (set == null)
            {
                Log.outError(LogFilter.Sql, "Item set {0} for item (id {1}) not found, mods not applied.", setid, proto.GetId());
                return;
            }

            if (set.RequiredSkill != 0 && player.GetSkillValue((SkillType)set.RequiredSkill) < set.RequiredSkillRank)
                return;

            if (set.SetFlags.HasAnyFlag(ItemSetFlags.LegacyInactive))
                return;

            ItemSetEffect eff = null;
            for (int x = 0; x < player.ItemSetEff.Count; ++x)
            {
                if (player.ItemSetEff[x]?.ItemSetID == setid)
                {
                    eff = player.ItemSetEff[x];
                    break;
                }
            }

            if (eff == null)
            {
                eff = new ItemSetEffect();
                eff.ItemSetID = setid;

                int x = 0;
                for (; x < player.ItemSetEff.Count; ++x)
                    if (player.ItemSetEff[x] == null)
                        break;

                if (x < player.ItemSetEff.Count)
                    player.ItemSetEff[x] = eff;
                else
                    player.ItemSetEff.Add(eff);
            }

            ++eff.EquippedItemCount;

            List<ItemSetSpellRecord> itemSetSpells = Global.DB2Mgr.GetItemSetSpells(setid);
            foreach (var itemSetSpell in itemSetSpells)
            {
                //not enough for  spell
                if (itemSetSpell.Threshold > eff.EquippedItemCount)
                    continue;

                if (eff.SetBonuses.Contains(itemSetSpell))
                    continue;

                SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(itemSetSpell.SpellID);
                if (spellInfo == null)
                {
                    Log.outError(LogFilter.Player, "WORLD: unknown spell id {0} in items set {1} effects", itemSetSpell.SpellID, setid);
                    continue;
                }

                eff.SetBonuses.Add(itemSetSpell);
                // spell cast only if fit form requirement, in other case will cast at form change
                if (itemSetSpell.ChrSpecID == 0 || itemSetSpell.ChrSpecID == player.GetPrimarySpecialization())
                    player.ApplyEquipSpell(spellInfo, null, true);
            }
        }

        public static void RemoveItemsSetItem(Player player, ItemTemplate proto)
        {
            uint setid = proto.GetItemSet();

            ItemSetRecord set = CliDB.ItemSetStorage.LookupByKey(setid);
            if (set == null)
            {
                Log.outError(LogFilter.Sql, "Item set {0} for item {1} not found, mods not removed.", setid, proto.GetId());
                return;
            }

            ItemSetEffect eff = null;
            int setindex = 0;
            for (; setindex < player.ItemSetEff.Count; setindex++)
            {
                if (player.ItemSetEff[setindex] != null && player.ItemSetEff[setindex].ItemSetID == setid)
                {
                    eff = player.ItemSetEff[setindex];
                    break;
                }
            }

            // can be in case now enough skill requirement for set appling but set has been appliend when skill requirement not enough
            if (eff == null)
                return;

            --eff.EquippedItemCount;

            List<ItemSetSpellRecord> itemSetSpells = Global.DB2Mgr.GetItemSetSpells(setid);
            foreach (ItemSetSpellRecord itemSetSpell in itemSetSpells)
            {
                // enough for spell
                if (itemSetSpell.Threshold <= eff.EquippedItemCount)
                    continue;

                if (!eff.SetBonuses.Contains(itemSetSpell))
                    continue;

                player.ApplyEquipSpell(Global.SpellMgr.GetSpellInfo(itemSetSpell.SpellID), null, false);
                eff.SetBonuses.Remove(itemSetSpell);
            }

            if (eff.EquippedItemCount == 0)                                    //all items of a set were removed
            {
                Cypher.Assert(eff == player.ItemSetEff[setindex]);
                player.ItemSetEff[setindex] = null;
            }
        }

        public BonusData GetBonus() { return _bonusData; }

        public ObjectGuid GetOwnerGUID() { return m_itemData.Owner; }
        public void SetOwnerGUID(ObjectGuid guid) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.Owner), guid); }
        public ObjectGuid GetContainedIn()     { return m_itemData.ContainedIn; }
        public void SetContainedIn(ObjectGuid guid) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.ContainedIn), guid); }
        public ObjectGuid GetCreator()     { return m_itemData.Creator; }
        public void SetCreator(ObjectGuid guid) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.Creator), guid); }
        public ObjectGuid GetGiftCreator()     { return m_itemData.GiftCreator; }
        public void SetGiftCreator(ObjectGuid guid) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.GiftCreator), guid); }

        void SetExpiration(uint expiration) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.Expiration), expiration); }

        public ItemBondingType GetBonding() { return _bonusData.Bonding; }
        public void SetBinding(bool val)
        {
            if (val)
                AddItemFlag(ItemFieldFlags.Soulbound);
            else
                RemoveItemFlag(ItemFieldFlags.Soulbound);
        }

        public bool IsSoulBound() { return HasItemFlag(ItemFieldFlags.Soulbound); }
        public bool IsBoundAccountWide() { return GetTemplate().GetFlags().HasAnyFlag(ItemFlags.IsBoundToAccount); }
        public bool IsBattlenetAccountBound() { return GetTemplate().GetFlags2().HasAnyFlag(ItemFlags2.BnetAccountTradeOk); }

        public bool HasItemFlag(ItemFieldFlags flag) { return (m_itemData.DynamicFlags & (uint)flag) != 0; }
        public void AddItemFlag(ItemFieldFlags flags) { SetUpdateFieldFlagValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.DynamicFlags), (uint)flags); }
        public void RemoveItemFlag(ItemFieldFlags flags) { RemoveUpdateFieldFlagValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.DynamicFlags), (uint)flags); }
        public void SetItemFlags(ItemFieldFlags flags) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.DynamicFlags), (uint)flags); }
        public bool HasItemFlag2(ItemFieldFlags2 flag) { return (m_itemData.DynamicFlags2 & (uint)flag) != 0; }
        public void AddItemFlag2(ItemFieldFlags2 flags) { SetUpdateFieldFlagValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.DynamicFlags2), (uint)flags); }
        public void RemoveItemFlag2(ItemFieldFlags2 flags) { RemoveUpdateFieldFlagValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.DynamicFlags2), (uint)flags); }
        public void SetItemFlags2(ItemFieldFlags2 flags) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.DynamicFlags2), (uint)flags); }

        public Bag ToBag() { return IsBag() ? this as Bag : null; }
        public AzeriteItem ToAzeriteItem() { return IsAzeriteItem() ? this as AzeriteItem : null; }
        public AzeriteEmpoweredItem ToAzeriteEmpoweredItem() { return IsAzeriteEmpoweredItem() ? this as AzeriteEmpoweredItem : null; }

        public bool IsLocked() { return !HasItemFlag(ItemFieldFlags.Unlocked); }
        public bool IsBag() { return GetTemplate().GetInventoryType() == InventoryType.Bag; }
        public bool IsAzeriteItem() { return GetTypeId() == TypeId.AzeriteItem; }
        public bool IsAzeriteEmpoweredItem() { return GetTypeId() == TypeId.AzeriteEmpoweredItem; }
        public bool IsCurrencyToken() { return GetTemplate().IsCurrencyToken(); }
        public bool IsBroken() { return m_itemData.MaxDurability > 0 && m_itemData.Durability == 0; }
        public void SetDurability(uint durability) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.Durability), durability); }
        public void SetInTrade(bool b = true) { mb_in_trade = b; }
        public bool IsInTrade() { return mb_in_trade; }

        public uint GetCount() { return m_itemData.StackCount; }
        public uint GetMaxStackCount() { return GetTemplate().GetMaxStackSize(); }

        public byte GetSlot() { return m_slot; }
        public Bag GetContainer() { return m_container; }
        public void SetSlot(byte slot) { m_slot = slot; }
        public ushort GetPos() { return (ushort)(GetBagSlot() << 8 | GetSlot()); }
        public void SetContainer(Bag container) { m_container = container; }

        bool IsInBag() { return m_container != null; }

        public uint GetItemRandomBonusListId() { return m_randomBonusListId; }
        public uint GetEnchantmentId(EnchantmentSlot slot) { return m_itemData.Enchantment[(int)slot].ID; }
        public uint GetEnchantmentDuration(EnchantmentSlot slot) { return m_itemData.Enchantment[(int)slot].Duration; }
        public int GetEnchantmentCharges(EnchantmentSlot slot) { return m_itemData.Enchantment[(int)slot].Charges; }

        public void SetCreatePlayedTime(uint createPlayedTime) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.CreatePlayedTime), createPlayedTime); }

        public string GetText() { return m_text; }
        public void SetText(string text) { m_text = text; }

        public int GetSpellCharges(int index = 0) { return m_itemData.SpellCharges[index]; }
        public void SetSpellCharges(int index, int value) { SetUpdateFieldValue(ref m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.SpellCharges, index), value); }

        public ItemUpdateState GetState() { return uState; }

        public bool IsInUpdateQueue() { return uQueuePos != -1; }
        public int GetQueuePos() { return uQueuePos; }
        public void FSetState(ItemUpdateState state)// forced
        {
            uState = state;
        }

        public override bool HasQuest(uint quest_id) { return GetTemplate().GetStartQuest() == quest_id; }
        public override bool HasInvolvedQuest(uint quest_id) { return false; }
        public bool IsPotion() { return GetTemplate().IsPotion(); }
        public bool IsVellum() { return GetTemplate().IsVellum(); }
        public bool IsConjuredConsumable() { return GetTemplate().IsConjuredConsumable(); }
        public bool IsRangedWeapon() { return GetTemplate().IsRangedWeapon(); }
        public ItemQuality GetQuality() { return _bonusData.Quality; }
        public int GetItemStatType(uint index)
        {
            Cypher.Assert(index < ItemConst.MaxStats);
            return _bonusData.ItemStatType[index];
        }
        public SocketColor GetSocketColor(uint index)
        {
            Cypher.Assert(index < ItemConst.MaxGemSockets);
            return _bonusData.socketColor[index];
        }
        public uint GetAppearanceModId() { return m_itemData.ItemAppearanceModID; }
        public void SetAppearanceModId(uint appearanceModId) { SetUpdateFieldValue(m_values.ModifyValue(m_itemData).ModifyValue(m_itemData.ItemAppearanceModID), (byte)appearanceModId); }
        public uint GetArmor(Player owner) { return GetTemplate().GetArmor(GetItemLevel(owner)); }
        public void GetDamage(Player owner, out float minDamage, out float maxDamage) { GetTemplate().GetDamage(GetItemLevel(owner), out minDamage, out maxDamage); }
        public float GetRepairCostMultiplier() { return _bonusData.RepairCostMultiplier; }
        public uint GetScalingStatDistribution() { return _bonusData.ScalingStatDistribution; }

        public void SetRefundRecipient(ObjectGuid guid) { m_refundRecipient = guid; }
        public void SetPaidMoney(ulong money) { m_paidMoney = money; }
        public void SetPaidExtendedCost(uint iece) { m_paidExtendedCost = iece; }

        public ObjectGuid GetRefundRecipient() { return m_refundRecipient; }
        public ulong GetPaidMoney() { return m_paidMoney; }
        public uint GetPaidExtendedCost() { return m_paidExtendedCost; }

        public uint GetScriptId() { return GetTemplate().ScriptId; }

        public ObjectGuid GetChildItem() { return m_childItem; }
        public void SetChildItem(ObjectGuid childItem) { m_childItem = childItem; }

        //Static
        public static bool ItemCanGoIntoBag(ItemTemplate pProto, ItemTemplate pBagProto)
        {
            if (pProto == null || pBagProto == null)
                return false;

            switch (pBagProto.GetClass())
            {
                case ItemClass.Container:
                    switch ((ItemSubClassContainer)pBagProto.GetSubClass())
                    {
                        case ItemSubClassContainer.Container:
                            return true;
                        case ItemSubClassContainer.SoulContainer:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.SoulShards))
                                return false;
                            return true;
                        case ItemSubClassContainer.HerbContainer:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.Herbs))
                                return false;
                            return true;
                        case ItemSubClassContainer.EnchantingContainer:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.EnchantingSupp))
                                return false;
                            return true;
                        case ItemSubClassContainer.MiningContainer:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.MiningSupp))
                                return false;
                            return true;
                        case ItemSubClassContainer.EngineeringContainer:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.EngineeringSupp))
                                return false;
                            return true;
                        case ItemSubClassContainer.GemContainer:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.Gems))
                                return false;
                            return true;
                        case ItemSubClassContainer.LeatherworkingContainer:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.LeatherworkingSupp))
                                return false;
                            return true;
                        case ItemSubClassContainer.InscriptionContainer:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.InscriptionSupp))
                                return false;
                            return true;
                        case ItemSubClassContainer.TackleContainer:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.FishingSupp))
                                return false;
                            return true;
                        case ItemSubClassContainer.CookingContainer:
                            if (!pProto.GetBagFamily().HasAnyFlag(BagFamilyMask.CookingSupp))
                                return false;
                            return true;
                        default:
                            return false;
                    }
                //can remove?
                case ItemClass.Quiver:
                    switch ((ItemSubClassQuiver)pBagProto.GetSubClass())
                    {
                        case ItemSubClassQuiver.Quiver:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.Arrows))
                                return false;
                            return true;
                        case ItemSubClassQuiver.AmmoPouch:
                            if (!Convert.ToBoolean(pProto.GetBagFamily() & BagFamilyMask.Bullets))
                                return false;
                            return true;
                        default:
                            return false;
                    }
            }
            return false;
        }

        public static uint ItemSubClassToDurabilityMultiplierId(ItemClass ItemClass, uint ItemSubClass)
        {
            switch (ItemClass)
            {
                case ItemClass.Weapon: return ItemSubClass;
                case ItemClass.Armor: return ItemSubClass + 21;
            }
            return 0;
        }

        #region Fields
        public ItemData m_itemData;

        public bool m_lootGenerated;
        public Loot loot;
        internal BonusData _bonusData;

        ItemUpdateState uState;
        uint m_paidExtendedCost;
        ulong m_paidMoney;
        ObjectGuid m_refundRecipient;
        byte m_slot;
        Bag m_container;
        int uQueuePos;
        string m_text;
        bool mb_in_trade;
        long m_lastPlayedTimeUpdate;
        List<ObjectGuid> allowedGUIDs = new List<ObjectGuid>();
        uint m_randomBonusListId;        // store separately to easily find which bonus list is the one randomly given for stat rerolling
        ObjectGuid m_childItem;
        Dictionary<uint, ushort> m_artifactPowerIdToIndex = new Dictionary<uint, ushort>();
        Array<uint> m_gemScalingLevels = new Array<uint>(ItemConst.MaxGemSockets);
        #endregion
    }

    public class ItemPosCount
    {
        public ItemPosCount(ushort _pos, uint _count)
        {
            pos = _pos;
            count = _count;
        }

        public bool IsContainedIn(List<ItemPosCount> vec)
        {
            foreach (var posCount in vec)
                if (posCount.pos == pos)
                    return true;
            return false;
        }

        public ushort pos;
        public uint count;
    }

    public enum EnchantmentOffset
    {
        Id = 0,
        Duration = 1,
        Charges = 2,                         // now here not only charges, but something new in wotlk
        Max = 3
    }

    public class ItemSetEffect
    {
        public uint ItemSetID;
        public uint EquippedItemCount;
        public List<ItemSetSpellRecord> SetBonuses = new List<ItemSetSpellRecord>();
    }

    public class BonusData
    {
        public BonusData(ItemTemplate proto)
        {
            if (proto == null)
                return;

            Quality = proto.GetQuality();
            ItemLevelBonus = 0;
            RequiredLevel = proto.GetBaseRequiredLevel();
            for (uint i = 0; i < ItemConst.MaxStats; ++i)
                ItemStatType[i] = proto.GetItemStatType(i);

            for (uint i = 0; i < ItemConst.MaxStats; ++i)
                ItemStatAllocation[i] = proto.GetItemStatAllocation(i);

            for (uint i = 0; i < ItemConst.MaxStats; ++i)
                ItemStatSocketCostMultiplier[i] = proto.GetItemStatSocketCostMultiplier(i);

            for (uint i = 0; i < ItemConst.MaxGemSockets; ++i)
            {
                socketColor[i] = proto.GetSocketColor(i);
                GemItemLevelBonus[i] = 0;
                GemRelicType[i] = -1;
                GemRelicRankBonus[i] = 0;
            }

            Bonding = proto.GetBonding();

            AppearanceModID = 0;
            RepairCostMultiplier = 1.0f;
            ScalingStatDistribution = proto.GetScalingStatDistribution();
            RelicType = -1;
            HasFixedLevel = false;
            RequiredLevelOverride = 0;
            AzeriteTierUnlockSetId = 0;

            AzeriteEmpoweredItemRecord azeriteEmpoweredItem = Global.DB2Mgr.GetAzeriteEmpoweredItem(proto.GetId());
            if (azeriteEmpoweredItem != null)
                AzeriteTierUnlockSetId = azeriteEmpoweredItem.AzeriteTierUnlockSetID;

            CanDisenchant = !proto.GetFlags().HasAnyFlag(ItemFlags.NoDisenchant);
            CanScrap = proto.GetFlags4().HasAnyFlag(ItemFlags4.Scrapable);

            _state.AppearanceModPriority = int.MaxValue;
            _state.ScalingStatDistributionPriority = int.MaxValue;
            _state.AzeriteTierUnlockSetPriority = int.MaxValue;
            _state.HasQualityBonus = false;
        }

        public BonusData(ItemInstance itemInstance) : this(Global.ObjectMgr.GetItemTemplate(itemInstance.ItemID))
        {
            if (itemInstance.ItemBonus.HasValue)
            {
                foreach (uint bonusListID in itemInstance.ItemBonus.Value.BonusListIDs)
                    AddBonusList(bonusListID);
            }
        }

        public void AddBonusList(uint bonusListId)
        {
            var bonuses = Global.DB2Mgr.GetItemBonusList(bonusListId);
            if (bonuses != null)
                foreach (ItemBonusRecord bonus in bonuses)
                    AddBonus(bonus.BonusType, bonus.Value);
        }

        public void AddBonus(ItemBonusType type, int[] values)
        {
            switch (type)
            {
                case ItemBonusType.ItemLevel:
                    ItemLevelBonus += values[0];
                    break;
                case ItemBonusType.Stat:
                    {
                        uint statIndex = 0;
                        for (statIndex = 0; statIndex < ItemConst.MaxStats; ++statIndex)
                            if (ItemStatType[statIndex] == values[0] || ItemStatType[statIndex] == -1)
                                break;

                        if (statIndex < ItemConst.MaxStats)
                        {
                            ItemStatType[statIndex] = values[0];
                            ItemStatAllocation[statIndex] += values[1];
                        }
                        break;
                    }
                case ItemBonusType.Quality:
                    if (!_state.HasQualityBonus)
                    {
                        Quality = (ItemQuality)values[0];
                        _state.HasQualityBonus = true;
                    }
                    else if ((uint)Quality < values[0])
                        Quality = (ItemQuality)values[0];
                    break;
                case ItemBonusType.Socket:
                    {
                        uint socketCount = (uint)values[0];
                        for (uint i = 0; i < ItemConst.MaxGemSockets && socketCount != 0; ++i)
                        {
                            if (socketColor[i] == 0)
                            {
                                socketColor[i] = (SocketColor)values[1];
                                --socketCount;
                            }
                        }
                        break;
                    }
                case ItemBonusType.Appearance:
                    if (values[1] < _state.AppearanceModPriority)
                    {
                        AppearanceModID = Convert.ToUInt32(values[0]);
                        _state.AppearanceModPriority = values[1];
                    }
                    break;
                case ItemBonusType.RequiredLevel:
                    RequiredLevel += values[0];
                    break;
                case ItemBonusType.RepairCostMuliplier:
                    RepairCostMultiplier *= Convert.ToSingle(values[0]) * 0.01f;
                    break;
                case ItemBonusType.ScalingStatDistribution:
                case ItemBonusType.ScalingStatDistributionFixed:
                    if (values[1] < _state.ScalingStatDistributionPriority)
                    {
                        ScalingStatDistribution = (uint)values[0];
                        ContentTuningId = (uint)values[2];
                        _state.ScalingStatDistributionPriority = values[1];
                        HasFixedLevel = type == ItemBonusType.ScalingStatDistributionFixed;
                    }
                    break;
                case ItemBonusType.Bounding:
                    Bonding = (ItemBondingType)values[0];
                    break;
                case ItemBonusType.RelicType:
                    RelicType = values[0];
                    break;
                case ItemBonusType.OverrideRequiredLevel:
                    RequiredLevelOverride = values[0];
                    break;
                case ItemBonusType.AzeriteTierUnlockSet:
                    if (values[1] < _state.AzeriteTierUnlockSetPriority)
                    {
                        AzeriteTierUnlockSetId = (uint)values[0];
                        _state.AzeriteTierUnlockSetPriority = values[1];
                    }
                    break;
                case ItemBonusType.OverrideCanDisenchant:
                    CanDisenchant = values[0] != 0;
                    break;
                case ItemBonusType.OverrideCanScrap:
                    CanScrap = values[0] != 0;
                    break;
            }
        }

        public ItemQuality Quality;
        public int ItemLevelBonus;
        public int RequiredLevel;
        public int[] ItemStatType = new int[ItemConst.MaxStats];
        public int[] ItemStatAllocation = new int[ItemConst.MaxStats];
        public float[] ItemStatSocketCostMultiplier = new float[ItemConst.MaxStats];
        public SocketColor[] socketColor = new SocketColor[ItemConst.MaxGemSockets];
        public ItemBondingType Bonding;
        public uint AppearanceModID;
        public float RepairCostMultiplier;
        public uint ScalingStatDistribution;
        public uint ContentTuningId;
        public uint DisenchantLootId;
        public uint[] GemItemLevelBonus = new uint[ItemConst.MaxGemSockets];
        public int[] GemRelicType = new int[ItemConst.MaxGemSockets];
        public ushort[] GemRelicRankBonus = new ushort[ItemConst.MaxGemSockets];
        public int RelicType;
        public int RequiredLevelOverride;
        public uint AzeriteTierUnlockSetId;
        public bool CanDisenchant;
        public bool CanScrap;
        public bool HasFixedLevel;
        State _state;

        struct State
        {
            public int AppearanceModPriority;
            public int ScalingStatDistributionPriority;
            public int AzeriteTierUnlockSetPriority;
            public bool HasQualityBonus;
        }
    }

    public class ArtifactPowerData
    {
        public uint ArtifactPowerId;
        public byte PurchasedRank;
        public byte CurrentRankWithBonus;
    }

    class ArtifactData
    {
        public ulong Xp;
        public uint ArtifactAppearanceId;
        public uint ArtifactTierId;
        public List<ArtifactPowerData> ArtifactPowers = new List<ArtifactPowerData>();
    }

    public class AzeriteEmpoweredData
    {
        public Array<int> SelectedAzeritePowers = new Array<int>(SharedConst.MaxAzeriteEmpoweredTier);
    }

    class ItemAdditionalLoadInfo
    {
        public ArtifactData Artifact;
        public AzeriteData AzeriteItem;
        public AzeriteEmpoweredData AzeriteEmpoweredItem;

        public static void Init(Dictionary<ulong, ItemAdditionalLoadInfo> loadInfo, SQLResult artifactResult, SQLResult azeriteItemResult, SQLResult azeriteItemMilestonePowersResult, 
            SQLResult azeriteItemUnlockedEssencesResult, SQLResult azeriteEmpoweredItemResult)
        {
            ItemAdditionalLoadInfo GetOrCreateLoadInfo(ulong guid)
            {
                if (!loadInfo.ContainsKey(guid))
                    loadInfo[guid] = new ItemAdditionalLoadInfo();

                return loadInfo[guid];
            }

            if (!artifactResult.IsEmpty())
            {
                do
                {
                    ItemAdditionalLoadInfo info = GetOrCreateLoadInfo(artifactResult.Read<ulong>(0));
                    if (info.Artifact == null)
                        info.Artifact = new ArtifactData();

                    info.Artifact.Xp = artifactResult.Read<ulong>(1);
                    info.Artifact.ArtifactAppearanceId = artifactResult.Read<uint>(2);
                    info.Artifact.ArtifactTierId = artifactResult.Read<uint>(3);

                    ArtifactPowerData artifactPowerData = new ArtifactPowerData();
                    artifactPowerData.ArtifactPowerId = artifactResult.Read<uint>(4);
                    artifactPowerData.PurchasedRank = artifactResult.Read<byte>(5);

                    ArtifactPowerRecord artifactPower = CliDB.ArtifactPowerStorage.LookupByKey(artifactPowerData.ArtifactPowerId);
                    if (artifactPower != null)
                    {
                        uint maxRank = artifactPower.MaxPurchasableRank;
                        // allow ARTIFACT_POWER_FLAG_FINAL to overflow maxrank here - needs to be handled in Item::CheckArtifactUnlock (will refund artifact power)
                        if (artifactPower.Flags.HasAnyFlag(ArtifactPowerFlag.MaxRankWithTier) && artifactPower.Tier < info.Artifact.ArtifactTierId)
                            maxRank += info.Artifact.ArtifactTierId - artifactPower.Tier;

                        if (artifactPowerData.PurchasedRank > maxRank)
                            artifactPowerData.PurchasedRank = (byte)maxRank;

                        artifactPowerData.CurrentRankWithBonus = (byte)((artifactPower.Flags & ArtifactPowerFlag.First) == ArtifactPowerFlag.First ? 1 : 0);

                        info.Artifact.ArtifactPowers.Add(artifactPowerData);
                    }

                } while (artifactResult.NextRow());
            }

            if (!azeriteItemResult.IsEmpty())
            {
                do
                {
                    ItemAdditionalLoadInfo info = GetOrCreateLoadInfo(azeriteItemResult.Read<ulong>(0));
                    if (info.AzeriteItem == null)
                        info.AzeriteItem = new AzeriteData();

                    info.AzeriteItem.Xp = azeriteItemResult.Read<ulong>(1);
                    info.AzeriteItem.Level = azeriteItemResult.Read<uint>(2);
                    info.AzeriteItem.KnowledgeLevel = azeriteItemResult.Read<uint>(3);
                    for (int i = 0; i < PlayerConst.MaxSpecializations; ++i)
                    {
                        uint specializationId = azeriteItemResult.Read<uint>(4 + i * 4);
                        if (!CliDB.ChrSpecializationStorage.ContainsKey(specializationId))
                            continue;

                        info.AzeriteItem.SelectedAzeriteEssences[i].SpecializationId = specializationId;
                        for (int j = 0; j < SharedConst.MaxAzeriteEssenceSlot; ++j)
                        {
                            AzeriteEssenceRecord azeriteEssence = CliDB.AzeriteEssenceStorage.LookupByKey(azeriteItemResult.Read<uint>(5 + i * 4 + j));
                            if (azeriteEssence == null || !Global.DB2Mgr.IsSpecSetMember(azeriteEssence.SpecSetID, specializationId))
                                continue;

                            info.AzeriteItem.SelectedAzeriteEssences[i].AzeriteEssenceId[j] = azeriteEssence.Id;
                        }
                    }

                } while (azeriteItemResult.NextRow());
            }

            if (!azeriteItemMilestonePowersResult.IsEmpty())
            {
                do
                {
                    ItemAdditionalLoadInfo info = GetOrCreateLoadInfo(azeriteItemMilestonePowersResult.Read<ulong>(0));
                    if (info.AzeriteItem == null)
                        info.AzeriteItem = new AzeriteData();

                    info.AzeriteItem.AzeriteItemMilestonePowers.Add(azeriteItemMilestonePowersResult.Read<uint>(1));
                }
                while (azeriteItemMilestonePowersResult.NextRow());
            }

            if (!azeriteItemUnlockedEssencesResult.IsEmpty())
            {
                do
                {
                    AzeriteEssencePowerRecord azeriteEssencePower = Global.DB2Mgr.GetAzeriteEssencePower(azeriteItemUnlockedEssencesResult.Read<uint>(1), azeriteItemUnlockedEssencesResult.Read<uint>(2));
                    if (azeriteEssencePower != null)
                    {
                        ItemAdditionalLoadInfo info = GetOrCreateLoadInfo(azeriteItemUnlockedEssencesResult.Read<ulong>(0));
                        if (info.AzeriteItem == null)
                            info.AzeriteItem = new AzeriteData();

                        info.AzeriteItem.UnlockedAzeriteEssences.Add(azeriteEssencePower);
                    }
                }
                while (azeriteItemUnlockedEssencesResult.NextRow());
            }

            if (!azeriteEmpoweredItemResult.IsEmpty())
            {
                do
                {
                    ItemAdditionalLoadInfo info = GetOrCreateLoadInfo(azeriteEmpoweredItemResult.Read<ulong>(0));
                    if (info.AzeriteEmpoweredItem == null)
                        info.AzeriteEmpoweredItem = new AzeriteEmpoweredData();

                    for (int i = 0; i < SharedConst.MaxAzeriteEmpoweredTier; ++i)
                        if (CliDB.AzeritePowerStorage.ContainsKey(azeriteEmpoweredItemResult.Read<int>(1 + i)))
                            info.AzeriteEmpoweredItem.SelectedAzeritePowers[i] = azeriteEmpoweredItemResult.Read<int>(1 + i);

                } while (azeriteEmpoweredItemResult.NextRow());
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public class ItemDynamicFieldGems
    {
        public uint ItemId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ushort[] BonusListIDs = new ushort[16];
        public byte Context;
    }
}
