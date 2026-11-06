using HarmonyLib;
using UnityEngine;

namespace Oddhatter.MachineArbalest
{
    /// <summary>The per-step input hook that drives automatic fire.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.PlayerAttackInput))]
    internal static class Player_PlayerAttackInput_Patch
    {
        private static bool Prefix(Player __instance, out AutoFire.InputOverride __state)
        {
            return AutoFire.BeforeAttackInput(__instance, out __state);
        }

        // Harmony runs postfixes even when the prefix skipped the original.
        private static void Postfix(Player __instance, AutoFire.InputOverride __state)
        {
            AutoFire.AfterAttackInput(__instance, __state);
        }
    }

    /// <summary>
    /// Damage/recoil multipliers for shots that go through vanilla Attack.Start (UseVanillaReload = true).
    /// Humanoid.StartAttack clones a fresh Attack before every Start call, so this never compounds.
    /// Machine-mode shots don't call Start; AutoFire applies the same multipliers itself.
    /// </summary>
    [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
    internal static class Attack_Start_Patch
    {
        private static void Prefix(Attack __instance, Humanoid character, ItemDrop.ItemData weapon)
        {
            if (WeaponRules.AppliesTo(character, weapon))
            {
                WeaponRules.ApplyMultipliers(__instance);
            }
        }
    }

    /// <summary>
    /// StaminaCostMultiplier. Attack.GetAttackStamina is the single source vanilla uses for the
    /// "enough stamina?" check (Attack.Start), the charge (Attack.Update / FireProjectileBurst),
    /// and machine-mode shots, so scaling it here covers every firing path.
    /// </summary>
    [HarmonyPatch(typeof(Attack), nameof(Attack.GetAttackStamina))]
    internal static class Attack_GetAttackStamina_Patch
    {
        private static void Postfix(Attack __instance, ref float __result)
        {
            if (__result > 0f && WeaponRules.AppliesTo(__instance.m_character, __instance.m_weapon))
            {
                __result *= MachineArbalestPlugin.StaminaCostMultiplier.Value;
            }
        }
    }

    /// <summary>
    /// EitrCostMultiplier. This overload is the single source of a weapon's eitr cost: Attack's private wrapper (used by
    /// Attack.Start, Attack.Update and FireProjectileBurst, and by machine-mode shots) and Player's hotbar checks call it.
    /// </summary>
    [HarmonyPatch(typeof(Attack), nameof(Attack.GetAttackEitr), new[] { typeof(Character), typeof(ItemDrop.ItemData) })]
    internal static class Attack_GetAttackEitr_Patch
    {
        private static void Postfix(Character character, ItemDrop.ItemData weapon, ref float __result)
        {
            if (__result > 0f && WeaponRules.AppliesTo(character, weapon))
            {
                __result *= MachineArbalestPlugin.EitrCostMultiplier.Value;
            }
        }
    }

    /// <summary>
    /// StaminaCostMultiplier / EitrCostMultiplier also scale the stamina and eitr drained while reloading (UseVanillaReload = true;
    /// machine mode never reloads).
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.QueueReloadAction))]
    internal static class Player_QueueReloadAction_Patch
    {
        private static void Prefix(Player __instance, out int __state)
        {
            __state = __instance.m_actionQueue.Count;
        }

        private static void Postfix(Player __instance, int __state)
        {
            var queue = __instance.m_actionQueue;
            if (queue.Count <= __state)
            {
                return; // nothing was queued
            }
            Player.MinorActionData action = queue[queue.Count - 1];
            if (action.m_type == Player.MinorActionData.ActionType.Reload
                && WeaponRules.AppliesTo(__instance, action.m_item))
            {
                action.m_staminaDrain *= MachineArbalestPlugin.StaminaCostMultiplier.Value;
                action.m_eitrDrain *= MachineArbalestPlugin.EitrCostMultiplier.Value;
            }
        }
    }

    /// <summary>
    /// DurabilityMultiplier. Attack.ProjectileAttackTriggered is the only place a projectile shot wears the
    /// weapon (m_useDurabilityDrain * Game.m_durabilityRate, players only). Runs for machine-mode and
    /// vanilla-reload shots alike, and scales whatever vanilla drained (world modifiers included).
    /// </summary>
    [HarmonyPatch(typeof(Attack), nameof(Attack.ProjectileAttackTriggered))]
    internal static class Attack_ProjectileAttackTriggered_Patch
    {
        private static void Prefix(Attack __instance, out float __state)
        {
            __state = __instance.m_weapon != null ? __instance.m_weapon.m_durability : 0f;
        }

        private static void Postfix(Attack __instance, float __state)
        {
            ItemDrop.ItemData weapon = __instance.m_weapon;
            if (weapon == null || !WeaponRules.AppliesTo(__instance.m_character, weapon))
            {
                return;
            }
            float lost = __state - weapon.m_durability;
            if (lost <= 0f)
            {
                return;
            }
            float scaled = __state - lost * MachineArbalestPlugin.DurabilityMultiplier.Value;
            // Extra wear (multiplier > 1) stops at 0; never undo below-zero values vanilla itself produced.
            weapon.m_durability = Mathf.Max(scaled, Mathf.Min(weapon.m_durability, 0f));
        }
    }

    /// <summary>
    /// AmmoCostMultiplier. Attack.UseAmmo (run from OnAttackTrigger, for machine-mode and vanilla shots alike) is where a
    /// shot removes one bolt/arrow. Shots are counted, not rolled: at 0.1 exactly every 10th shot uses one. A free shot
    /// picks the ammo exactly as vanilla does and sets m_ammoItem (the projectile takes its damage from it), but doesn't
    /// remove it. With no ammo, or consumable ammo, vanilla runs unchanged.
    /// </summary>
    [HarmonyPatch(typeof(Attack), nameof(Attack.UseAmmo))]
    internal static class Attack_UseAmmo_Patch
    {
        /// <summary>Ammo owed by previous free shots; a shot that brings it to 1 uses a bolt.</summary>
        private static float s_owed;

        private static bool Prefix(Attack __instance, ref ItemDrop.ItemData ammoItem, ref bool __result)
        {
            ItemDrop.ItemData weapon = __instance.m_weapon;
            float multiplier = MachineArbalestPlugin.AmmoCostMultiplier.Value;
            if (multiplier >= 1f || weapon == null || string.IsNullOrWhiteSpace(weapon.m_shared.m_ammoType)
                || !WeaponRules.AppliesTo(__instance.m_character, weapon))
            {
                return true;
            }
            ItemDrop.ItemData ammo = Attack.FindAmmo(__instance.m_character as Humanoid, weapon);
            if (ammo == null || ammo.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable)
            {
                return true;
            }

            s_owed += multiplier;
            if (s_owed >= 1f - 0.0001f)
            {
                s_owed = Mathf.Max(0f, s_owed - 1f);
                Diag.Log("ammo", $"Ammo: {ammo.m_shared.m_name} used (AmmoCostMultiplier {multiplier:0.###})");
                return true; // vanilla removes one
            }
            Diag.Log("ammo", $"Ammo: {ammo.m_shared.m_name} free shot (AmmoCostMultiplier {multiplier:0.###})");
            __instance.m_ammoItem = ammo;
            ammoItem = ammo;
            __result = true;
            return false;
        }
    }
}
