namespace Oddhatter.MachineArbalest
{
    /// <summary>Which weapons the mod affects, and how an affected attack is modified.</summary>
    internal static class WeaponRules
    {
        internal enum Category { None, Crossbow, Bow, Staff }

        /// <summary>Arbalest's shared item name (confirmed in Valheim's localization table).</summary>
        private const string ArbalestToken = "$item_crossbow_arbalest";

        /// <summary>The weapon's kind, by the skill it trains. Elemental magic covers Dundr, Embers, Frost, Fracturing.</summary>
        public static Category GetCategory(ItemDrop.ItemData weapon)
        {
            if (weapon == null)
            {
                return Category.None;
            }
            switch (weapon.m_shared.m_skillType)
            {
                case Skills.SkillType.Crossbows: return Category.Crossbow;
                case Skills.SkillType.Bows: return Category.Bow;
                case Skills.SkillType.ElementalMagic: return Category.Staff;
                default: return Category.None;
            }
        }

        public static bool IsAffected(ItemDrop.ItemData weapon)
        {
            if (weapon == null || !MachineArbalestPlugin.Enabled.Value)
            {
                return false;
            }
            switch (GetCategory(weapon))
            {
                case Category.Crossbow:
                    return MachineArbalestPlugin.Crossbows.Value
                        && (!MachineArbalestPlugin.OnlyArbalest.Value || weapon.m_shared.m_name == ArbalestToken);
                case Category.Bow:
                    return MachineArbalestPlugin.Bows.Value;
                case Category.Staff:
                    return MachineArbalestPlugin.ElementalStaffs.Value;
                default:
                    return false;
            }
        }

        /// <summary>True when <paramref name="character"/> is the local player wielding an affected weapon.</summary>
        public static bool AppliesTo(Character character, ItemDrop.ItemData weapon)
        {
            return character != null && character == Player.m_localPlayer && IsAffected(weapon);
        }

        /// <summary>Seconds between machine-mode shots for this weapon.</summary>
        public static float FireInterval(ItemDrop.ItemData weapon)
        {
            switch (GetCategory(weapon))
            {
                case Category.Bow: return MachineArbalestPlugin.BowFireInterval.Value;
                case Category.Staff: return MachineArbalestPlugin.StaffFireInterval.Value;
                default: return MachineArbalestPlugin.FireInterval.Value;
            }
        }

        /// <summary>
        /// Vanilla-reload pacing (UseVanillaReload) only means something for crossbows. Bows fire on release in vanilla
        /// and staffs already re-cast while held, so both always use machine mode.
        /// </summary>
        public static bool UsesVanillaReload(ItemDrop.ItemData weapon)
        {
            return MachineArbalestPlugin.UseVanillaReload.Value && GetCategory(weapon) == Category.Crossbow;
        }

        /// <summary>
        /// Applies the damage and recoil multipliers to a freshly cloned Attack.
        /// Both fields are read by vanilla later (Attack.ModifyDamage / Attack.OnAttackTrigger).
        /// </summary>
        public static void ApplyMultipliers(Attack attack)
        {
            attack.m_damageMultiplier *= MachineArbalestPlugin.DamageMultiplier.Value;
            attack.m_recoilPushback *= MachineArbalestPlugin.RecoilMultiplier.Value;
        }
    }
}
