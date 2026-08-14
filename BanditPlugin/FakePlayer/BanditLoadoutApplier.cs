using System;
using SDG.Unturned;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// Dresses and arms a freshly spawned bot from the configured loadout.
    ///
    /// Everything here goes through the same public server-side entry points a real player's
    /// requests end up in, so replication is vanilla's problem rather than ours:
    ///   - PlayerClothing.askWear*(asset, quality, state, playEffect) sends the item to every
    ///     client via SendWear*.InvokeAndLoopback(... GatherRemoteClientConnections() ...) and
    ///     applies the server-side state that DamageTool.getPlayerArmor() reads, so a helmet on a
    ///     bandit is not just a hat you can see - it really does soak damage.
    ///   - PlayerInventory.tryAddItem(item, 255, 255, page, 0) puts a weapon in an equipment slot
    ///     and calls equipment.sendSlot(page), which is what puts it on the bot's back.
    /// Clients that connect later are covered too: Player.SendInitialPlayerState() forwards the
    /// clothing and slot state to each new joiner.
    ///
    /// Nothing here equips the weapon - BanditBotController does that on its own schedule, because
    /// PlayerEquipment.ServerEquip() silently no-ops during the moments right after spawn when the
    /// player is not yet in an equippable state, and because the bot swaps weapons later anyway.
    /// </summary>
    public static class BanditLoadoutApplier
    {
        /// <summary>PlayerInventory page for the primary weapon slot. Vanilla PlayerInventory.SLOTS
        /// is 2, and the two slot pages come first.</summary>
        public const byte PrimarySlotPage = 0;

        /// <summary>PlayerInventory page for the secondary weapon slot.</summary>
        public const byte SecondarySlotPage = 1;

        private const byte MagazineIdStateIndex = 8; // state[8..9] == attached magazine's legacy ID
        private const byte FiremodeStateIndex = 11;  // state[11] == EFiremode

        /// <summary>Which slots the bot actually ended up with something usable in.</summary>
        public struct Result
        {
            public bool HasPrimaryWeapon;
            public bool HasSecondaryWeapon;
        }

        public static Result Apply(Player player, BanditLoadout loadout)
        {
            Result result = default(Result);
            if (player == null || loadout == null)
            {
                return result;
            }

            // Clothing before weapons: a vest or backpack resizes the inventory as it goes on, and
            // there is no reason to make vanilla shuffle items that are already placed.
            Wear(player, loadout.Hat, EItemType.HAT, "Hat");
            Wear(player, loadout.Mask, EItemType.MASK, "Mask");
            Wear(player, loadout.Vest, EItemType.VEST, "Vest");
            Wear(player, loadout.Shirt, EItemType.SHIRT, "Shirt");
            Wear(player, loadout.Pants, EItemType.PANTS, "Pants");
            Wear(player, loadout.Backpack, EItemType.BACKPACK, "Backpack");
            Wear(player, loadout.Glasses, EItemType.GLASSES, "Glasses");

            result.HasPrimaryWeapon = GiveWeapon(player, loadout.PrimaryWeapon, PrimarySlotPage, "PrimaryWeapon");
            result.HasSecondaryWeapon = GiveWeapon(player, loadout.SecondaryWeapon, SecondarySlotPage, "SecondaryWeapon");
            return result;
        }

        /// <summary>
        /// Rounds a full magazine holds for a given gun and item state.
        ///
        /// Read from the magazine actually attached (its legacy ID lives in state[8..9]) rather
        /// than from a single configured number, because the bot can be holding a rifle one moment
        /// and a sidearm the next: topping a seven-round pistol up to a rifle's thirty would put
        /// more in the magazine than the gun can hold. ammoMax is the fallback, and is what vanilla
        /// itself uses when it spawns an admin-given gun full.
        /// </summary>
        public static byte ResolveMagazineCapacity(ItemGunAsset gun, byte[] state)
        {
            if (state != null && state.Length > MagazineIdStateIndex + 1)
            {
                ushort magazineId = BitConverter.ToUInt16(state, MagazineIdStateIndex);
                if (magazineId != 0 && Assets.find(EAssetType.ITEM, magazineId) is ItemMagazineAsset magazine)
                {
                    return magazine.MaxAmountAsByte;
                }
            }

            return gun != null ? gun.ammoMax : (byte)0;
        }

        private static void Wear(Player player, BanditClothingItem entry, EItemType expectedType, string slotName)
        {
            ItemAsset asset = Resolve(entry?.Item, slotName);
            if (asset == null)
            {
                return;
            }

            // The slot an item goes in is decided by its own type, so a helmet GUID pasted into the
            // Mask line would silently be worn as a hat and quietly shadow whatever the Hat line
            // asked for. Say so instead.
            if (asset.type != expectedType)
            {
                Logger.LogError($"[Bandit] Loadout {slotName} resolves to '{asset.FriendlyName}', which is a {asset.type} rather than a {expectedType}; that slot is left empty.");
                return;
            }

            byte quality = entry.Quality > 100 ? (byte)100 : entry.Quality;
            byte[] state = asset.getState(EItemOrigin.ADMIN);

            // playEffect false: the wear sound belongs to a player rummaging through their bag, not
            // to a bandit that materialised already dressed.
            switch (asset)
            {
                case ItemHatAsset hat:
                    player.clothing.askWearHat(hat, quality, state, false);
                    break;
                case ItemMaskAsset mask:
                    player.clothing.askWearMask(mask, quality, state, false);
                    break;
                case ItemVestAsset vest:
                    player.clothing.askWearVest(vest, quality, state, false);
                    break;
                case ItemShirtAsset shirt:
                    player.clothing.askWearShirt(shirt, quality, state, false);
                    break;
                case ItemPantsAsset pants:
                    player.clothing.askWearPants(pants, quality, state, false);
                    break;
                case ItemBackpackAsset backpack:
                    player.clothing.askWearBackpack(backpack, quality, state, false);
                    break;
                case ItemGlassesAsset glasses:
                    player.clothing.askWearGlasses(glasses, quality, state, false);
                    break;
                default:
                    // EItemType said this was wearable but the asset is not the matching subclass,
                    // which would mean a game update moved things around.
                    Logger.LogError($"[Bandit] Loadout {slotName} '{asset.FriendlyName}' reports type {asset.type} but is a {asset.GetType().Name}; that slot is left empty.");
                    break;
            }
        }

        private static bool GiveWeapon(Player player, BanditWeapon entry, byte page, string slotName)
        {
            ItemAsset asset = Resolve(entry?.Item, slotName);
            if (asset == null)
            {
                return false;
            }

            ItemGunAsset gun = asset as ItemGunAsset;
            if (gun == null)
            {
                // Still handed over - it will show on the bot's back - but the combat code only
                // knows how to drive a UseableGun, so say plainly that it will never be used.
                Logger.LogWarning($"[Bandit] Loadout {slotName} '{asset.FriendlyName}' is a {asset.type}, not a gun. The bot only knows how to shoot, so it will carry this without ever attacking with it.");
            }

            Item item = new Item(asset.id, true);
            SetFiremode(gun, item);

            // x/y of 255 routes to Items.tryAddItem(item), the same call vanilla's own
            // tryAddItemEquip uses for slots - it skips the free-space search, which does not apply
            // to a slot page, and it is the branch that calls equipment.sendSlot(page).
            if (player.inventory.tryAddItem(item, byte.MaxValue, byte.MaxValue, page, 0))
            {
                return true;
            }

            // tryAddItem refuses a page below SLOTS unless asset.slot.canEquipInPage(page), so this
            // is nearly always a "Slot Primary" rifle configured as the secondary.
            Logger.LogError($"[Bandit] Loadout {slotName} '{asset.FriendlyName}' cannot go in that equipment slot (the asset's Slot is {asset.slot}); putting it in the bot's bag instead, where it will not be used.");
            player.inventory.forceAddItem(item, false);
            return false;
        }

        /// <summary>
        /// UseableGun.startPrimary() refuses to fire on SAFETY, which is a plausible default for an
        /// asset to ship with, and the bot pulls the trigger once per fire interval rather than
        /// holding it down - so semi is the mode that matches its cadence. Guns with no semi mode
        /// keep whatever the asset's own default state gave them, as long as that is not safety.
        /// </summary>
        private static void SetFiremode(ItemGunAsset gun, Item item)
        {
            if (gun == null || item.state == null || item.state.Length <= FiremodeStateIndex)
            {
                return;
            }

            if (gun.hasSemi)
            {
                item.state[FiremodeStateIndex] = (byte)EFiremode.SEMI;
                return;
            }

            if ((EFiremode)item.state[FiremodeStateIndex] != EFiremode.SAFETY)
            {
                return;
            }

            if (gun.hasBurst)
            {
                item.state[FiremodeStateIndex] = (byte)EFiremode.BURST;
            }
            else if (gun.hasAuto)
            {
                item.state[FiremodeStateIndex] = (byte)EFiremode.AUTO;
            }
        }

        /// <summary>
        /// Turns one configured identifier into an asset, accepting either form Unturned uses.
        ///
        /// The two can't be mistaken for each other, so no separate "which kind is this" setting is
        /// needed: a legacy ID is at most five digits, and a GUID is thirty-two hex characters that
        /// ushort.TryParse rejects outright. Guid.TryParse takes the dashless form the .dat files
        /// store as well as the dashed one, so either can be pasted in verbatim.
        ///
        /// A blank entry is an empty slot rather than a mistake and resolves to null silently.
        /// Anything that was configured and did not resolve is logged, because otherwise a typo'd
        /// identifier looks exactly like a slot that was deliberately left empty.
        /// </summary>
        private static ItemAsset Resolve(string identifier, string slotName)
        {
            string text = identifier?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            ItemAsset asset = null;

            if (ushort.TryParse(text, out ushort legacyId))
            {
                asset = legacyId != 0 ? Assets.find(EAssetType.ITEM, legacyId) as ItemAsset : null;
            }
            else if (Guid.TryParse(text, out Guid guid) && guid != Guid.Empty)
            {
                asset = Assets.find(guid) as ItemAsset;
            }
            else
            {
                Logger.LogError($"[Bandit] Loadout {slotName} is set to '{text}', which is neither an item ID nor a GUID; that slot is left empty.");
                return null;
            }

            if (asset == null)
            {
                Logger.LogError($"[Bandit] Loadout {slotName} '{text}' does not match any item asset on this server; that slot is left empty.");
            }

            return asset;
        }
    }
}
