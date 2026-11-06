using UnityEngine;

namespace Oddhatter.MachineArbalest
{
    /// <summary>
    /// Automatic fire, driven from Player.PlayerAttackInput (once per physics step, only for the
    /// owning, living local player — see Player.FixedUpdate).
    ///
    /// There is no coroutine or timer: a single static state block for the single local player is
    /// advanced each step, so repeated clicks can never stack multiple fire loops.
    /// </summary>
    internal static class AutoFire
    {
        /// <summary>Temporary input changes made for one PlayerAttackInput call and undone right after.</summary>
        internal struct InputOverride
        {
            public bool Active;
            public bool Attack;
            public bool AttackHold;
        }

        private enum ShotResult { Fired, Blocked, OutOfAmmo }

        private const float TimeEpsilon = 0.0001f;

        private static Player s_player;
        private static ItemDrop.ItemData s_weapon;
        private static bool s_firing;
        private static bool s_latchedUntilRelease;
        private static bool s_toggledOn;
        private static float s_nextShotTime;
        private static int s_burstShots; // diagnostics only

        /// <returns>true to let vanilla PlayerAttackInput run, false to skip it.</returns>
        public static bool BeforeAttackInput(Player player, out InputOverride inputOverride)
        {
            inputOverride = default;

            // PlayerAttackInput itself returns immediately in place (build) mode.
            ItemDrop.ItemData weapon = player.InPlaceMode() ? null : player.GetCurrentWeapon();
            bool affected = WeaponRules.IsAffected(weapon);
            if (player == Player.m_localPlayer)
            {
                Diag.Log("weapon", weapon == null
                    ? "Weapon: none"
                    : $"Weapon: {weapon.m_shared.m_name} (skill {weapon.m_shared.m_skillType}) " +
                      $"[attack stamina/eitr {weapon.m_shared.m_attack.m_attackStamina}/{weapon.m_shared.m_attack.m_attackEitr}, " +
                      $"reload drain {weapon.m_shared.m_attack.m_reloadStaminaDrain}/{weapon.m_shared.m_attack.m_reloadEitrDrain} per s, " +
                      $"draw drain {weapon.m_shared.m_attack.m_drawStaminaDrain}/{weapon.m_shared.m_attack.m_drawEitrDrain} per s] -> " +
                      (affected ? "affected" : "ignored (see the Weapons toggles and OnlyArbalest)"));
            }
            if (player != Player.m_localPlayer || !affected)
            {
                ResetState(null, null);
                return true;
            }
            if (player != s_player || weapon != s_weapon)
            {
                // New player instance (respawn) or a different weapon: the old loop is over.
                ResetState(player, weapon);
            }

            bool pressedThisStep = player.m_attack;
            bool wantFire = ReadTrigger(player);
            Diag.Log("trigger", $"Trigger: held={player.m_attackHold} want={wantFire} latched={s_latchedUntilRelease} " +
                                $"mode={(WeaponRules.UsesVanillaReload(weapon) ? "vanilla-reload" : "machine")}");

            if (WeaponRules.UsesVanillaReload(weapon))
            {
                s_firing = false;
                // Holding already re-fires in vanilla (Player.PlayerAttackInput: "m_attackHold && StartAttack").
                // Toggle mode only needs to emulate the held button while toggled on.
                if (!MachineArbalestPlugin.RequireHold.Value)
                {
                    inputOverride = new InputOverride { Active = true, Attack = player.m_attack, AttackHold = player.m_attackHold };
                    player.m_attackHold = wantFire;
                    if (!wantFire && pressedThisStep)
                    {
                        player.m_attack = false; // the click that toggled off must not queue one more shot
                    }
                    if (wantFire && !HasAmmo(player, weapon))
                    {
                        StopFiring(player, weapon);
                        player.m_attackHold = false;
                    }
                }
                return true;
            }

            MachineTick(player, weapon, wantFire);
            return false;
        }

        public static void AfterAttackInput(Player player, InputOverride inputOverride)
        {
            if (inputOverride.Active)
            {
                player.m_attack = inputOverride.Attack;
                player.m_attackHold = inputOverride.AttackHold;
            }
        }

        private static bool ReadTrigger(Player player)
        {
            if (MachineArbalestPlugin.RequireHold.Value)
            {
                s_toggledOn = false;
                // m_attackHold is already false whenever a menu, chat, console, inventory, map, etc. has focus
                // (PlayerController.FixedUpdate / TakeInput / InInventoryEtc).
                if (!player.m_attackHold)
                {
                    s_latchedUntilRelease = false;
                    return false;
                }
                return !s_latchedUntilRelease;
            }

            // m_attack is true only on the step the button goes down.
            if (player.m_attack)
            {
                s_toggledOn = !s_toggledOn;
            }
            if (s_toggledOn && IsUiOpen())
            {
                s_toggledOn = false;
            }
            return s_toggledOn;
        }

        private static void MachineTick(Player player, ItemDrop.ItemData weapon, bool wantFire)
        {
            KeepLoaded(player, weapon);

            if (!wantFire)
            {
                if (s_firing)
                {
                    Diag.Log("burst", $"Burst ended: {s_burstShots} shots");
                    Diag.Forget("shot");
                }
                s_firing = false;
                return;
            }

            float now = Time.fixedTime;
            if (!s_firing)
            {
                s_burstShots = 0;
                Diag.Forget("burst");
                s_firing = true;
                s_nextShotTime = now; // first shot fires on the press itself
            }
            if (now + TimeEpsilon < s_nextShotTime)
            {
                return;
            }

            ShotResult result = TryFireShot(player, weapon, out string blockedBy);
            Diag.Log("shot", result == ShotResult.Blocked ? $"Shot blocked: {blockedBy}" : $"Shot: {result}");
            switch (result)
            {
                case ShotResult.Fired:
                    s_burstShots++;
                    // Advance on a fixed grid so 0.10s really averages 10 shots/s (no per-step drift),
                    // but never schedule into the past after a stall (no catch-up bursts).
                    s_nextShotTime = Mathf.Max(s_nextShotTime + WeaponRules.FireInterval(weapon), now);
                    break;
                case ShotResult.Blocked:
                    s_nextShotTime = now; // retry next step once whatever blocked us clears
                    break;
                case ShotResult.OutOfAmmo:
                    StopFiring(player, weapon);
                    break;
            }
        }

        /// <summary>
        /// Machine mode has no reload: drop any queued reload (mirrors Player.ResetLoadedWeapon) and mark
        /// the weapon loaded (what the Reload minor action does when it completes).
        /// </summary>
        private static void KeepLoaded(Player player, ItemDrop.ItemData weapon)
        {
            if (!weapon.m_shared.m_attack.m_requiresReload)
            {
                return;
            }
            player.CancelReloadAction();
            player.SetWeaponLoaded(weapon);
        }

        /// <summary>
        /// One shot through the vanilla Attack pipeline. Mirrors the gates in Humanoid.StartAttack and
        /// Attack.Start, then uses Attack.StartWithoutAnimation, which runs the real Attack.OnAttackTrigger:
        /// ammo use, trigger effects, durability, projectile spawn with vanilla HitData/SEMan modifiers, recoil.
        /// The costs and start effects that vanilla applies in Attack.Update when the animation begins are
        /// applied here, since no attack animation is played.
        /// </summary>
        private static ShotResult TryFireShot(Player player, ItemDrop.ItemData weapon, out string blockedBy)
        {
            // Humanoid.StartAttack gates.
            blockedBy = player.InAttack() ? "attack animation"
                : player.InDodge() ? "dodging"
                : !player.CanMove() ? "can't move"
                : player.IsKnockedBack() ? "knocked back"
                : player.IsStaggering() ? "staggering"
                : player.InMinorAction() ? "minor action (equip/reload/eat animation)"
                : !weapon.HavePrimaryAttack() ? "weapon has no primary attack"
                : VolleyInProgress(player) ? "previous volley still firing"
                : null;
            if (blockedBy != null)
            {
                return ShotResult.Blocked;
            }

            Attack attack = weapon.m_shared.m_attack.Clone();
            attack.m_character = player;
            attack.m_weapon = weapon;
            // Reload state is managed by KeepLoaded; stop OnAttackTrigger from resetting it every shot.
            attack.m_requiresReload = false;
            // No looping animation plays, and Attack.Stop on a looping attack (Staff of Frost) fires the animator's
            // "attack_abort" trigger every time the next shot replaces it.
            attack.m_loopingAttack = false;
            WeaponRules.ApplyMultipliers(attack);

            // Attack.Start gates.
            if (attack.m_cantUseInDungeon && player.InInterior())
            {
                player.Message(MessageHud.MessageType.Center, "$msg_blocked");
                blockedBy = "can't use in dungeon";
                return ShotResult.Blocked;
            }
            // Attack costs are already scaled by the cost multipliers (see Patches). The cycle cost is the reload or draw
            // that vanilla would charge for this shot and machine mode skips.
            float attackStamina = attack.GetAttackStamina();
            float attackEitr = attack.GetAttackEitr();
            GetCycleCost(player, weapon, out float cycleStamina, out float cycleEitr);
            float stamina = attackStamina + cycleStamina;
            float eitr = attackEitr + cycleEitr;
            if (stamina > 0f && !player.HaveStamina(stamina + 0.1f))
            {
                Hud.instance?.StaminaBarEmptyFlash();
                blockedBy = $"not enough stamina (needs {stamina:0.#})";
                return ShotResult.Blocked;
            }
            if (!HasAmmo(player, weapon))
            {
                return ShotResult.OutOfAmmo;
            }
            // TryUseEitr only checks (and flashes the bar); the eitr is spent below, as in Attack.Update.
            if (!player.TryUseEitr(eitr))
            {
                blockedBy = "not enough eitr";
                return ShotResult.Blocked;
            }
            float health = attack.GetAttackHealth();
            if (health > 0f && attack.m_attackHealthLowBlockUse && !player.HaveHealth(health + 0.1f))
            {
                Hud.instance?.FlashHealthBar(); // vanilla only warns here, it doesn't block
            }
            Attack.EquipAmmoItem(player, weapon);

            // Attack.Start: projectile attacks turn the player to the look direction
            // (also makes recoil push straight back).
            player.transform.rotation = player.GetLookYaw();
            player.m_body.rotation = player.transform.rotation;

            // Become the current attack, as Humanoid.StartAttack does. Other mods (e.g. FastArbalest's
            // SEMan.ModifyAttack postfix) read m_currentAttack while the projectile is being set up.
            if (player.m_currentAttack != null)
            {
                player.m_currentAttack.Stop();
                player.m_previousAttack = player.m_currentAttack;
            }
            player.m_currentAttack = attack;
            player.m_currentAttackIsSecondary = false;
            player.m_lastCombatTimer = 0f;

            attack.m_zanim = player.m_zanim;
            attack.m_animEvent = player.m_animEvent;
            // Bows scale accuracy, speed and damage by how far they were drawn; there is no draw here, so fire at full draw.
            float drawPercentage = attack.m_bowDraw ? 1f : 0f;
            attack.StartWithoutAnimation(player, player.m_body, player.m_visEquipment, weapon, drawPercentage);

            // From Attack.Update (first frame in the attack animation). Weapons with per-burst resource usage
            // pay for each volley inside Attack.FireProjectileBurst instead.
            // Weapons with per-burst resource usage pay the attack cost per volley inside Attack.FireProjectileBurst.
            bool payAttackCost = !attack.m_perBurstResourceUsage;
            player.UseStamina(cycleStamina + (payAttackCost ? attackStamina : 0f));
            player.UseEitr(cycleEitr + (payAttackCost ? attackEitr : 0f));
            if (payAttackCost)
            {
                player.UseHealth(Mathf.Min(player.GetHealth() - 1f, health));
            }
            Diag.Log("cost", $"Shot cost: stamina {attackStamina:0.##} attack + {cycleStamina:0.##} reload/draw, " +
                             $"eitr {attackEitr:0.##} attack + {cycleEitr:0.##} reload/draw");
            Transform origin = attack.GetAttackOrigin();
            weapon.m_shared.m_startEffect.Create(origin.position, player.transform.rotation, origin, 1f, -1, player.GetZDOID());
            attack.m_startEffect.Create(origin.position, player.transform.rotation, origin, 1f, -1, player.GetZDOID());
            player.AddNoise(attack.m_attackStartNoise);

            return ShotResult.Fired;
        }

        /// <summary>
        /// What one vanilla fire cycle costs beyond the attack itself, and machine mode would otherwise skip:
        /// - Reload (crossbows): Player drains m_reloadStaminaDrain/m_reloadEitrDrain per second for GetWeaponLoadingTime().
        /// - Draw (bows, draw-style staffs): Player.UpdateAttackBowDraw drains GetDrawStaminaDrain/GetDrawEitrDrain per second
        ///   until fully drawn, which takes Humanoid.GetAttackDrawPercentage's draw duration. The stamina part gets the same
        ///   equipment and status-effect modifiers vanilla applies there.
        /// Scaled by StaminaCostMultiplier / EitrCostMultiplier.
        /// </summary>
        private static void GetCycleCost(Player player, ItemDrop.ItemData weapon, out float stamina, out float eitr)
        {
            Attack shared = weapon.m_shared.m_attack;
            stamina = 0f;
            eitr = 0f;
            if (shared.m_requiresReload)
            {
                float reloadTime = weapon.GetWeaponLoadingTime();
                stamina += Mathf.Max(0f, shared.m_reloadStaminaDrain) * reloadTime;
                eitr += Mathf.Max(0f, shared.m_reloadEitrDrain) * reloadTime;
            }
            if (shared.m_bowDraw)
            {
                float skillFactor = player.GetSkillFactor(weapon.m_shared.m_skillType);
                float drawTime = Mathf.Lerp(shared.m_drawDurationMin, shared.m_drawDurationMin * 0.2f, skillFactor);
                float drawStamina = weapon.GetDrawStaminaDrain() * drawTime;
                drawStamina += drawStamina * player.GetEquipmentAttackStaminaModifier();
                player.m_seman.ModifyAttackStaminaUsage(drawStamina, ref drawStamina);
                stamina += drawStamina;
                eitr += weapon.GetDrawEitrDrain() * drawTime;
            }
            stamina *= MachineArbalestPlugin.StaminaCostMultiplier.Value;
            eitr *= MachineArbalestPlugin.EitrCostMultiplier.Value;
        }

        /// <summary>
        /// Multi-volley weapons (m_projectileBursts > 1) fire their remaining volleys from Attack.Update on a
        /// timer. Starting the next shot replaces m_currentAttack and would cut those off, so wait for them.
        /// </summary>
        private static bool VolleyInProgress(Player player)
        {
            Attack current = player.m_currentAttack;
            return current != null
                && !current.m_attackDone
                && current.m_projectileAttackStarted
                && current.m_projectileBurstsFired < current.m_projectileBursts;
        }

        /// <summary>Same lookup as Attack.HaveAmmo, without its per-call "out of" message.</summary>
        private static bool HasAmmo(Player player, ItemDrop.ItemData weapon)
        {
            if (string.IsNullOrEmpty(weapon.m_shared.m_ammoType))
            {
                return true;
            }
            ItemDrop.ItemData ammo = Attack.FindAmmo(player, weapon);
            if (ammo == null)
            {
                return false;
            }
            return ammo.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable || player.CanConsumeItem(ammo);
        }

        /// <summary>Out of ammo: stop, and stay stopped until the button is released (hold) or clicked again (toggle).</summary>
        private static void StopFiring(Player player, ItemDrop.ItemData weapon)
        {
            s_firing = false;
            s_toggledOn = false;
            s_latchedUntilRelease = true;
            player.Message(MessageHud.MessageType.Center, "$msg_outof " + weapon.m_shared.m_ammoType);
        }

        private static void ResetState(Player player, ItemDrop.ItemData weapon)
        {
            s_player = player;
            s_weapon = weapon;
            s_firing = false;
            s_latchedUntilRelease = false;
            s_toggledOn = false;
            s_nextShotTime = 0f;
        }

        /// <summary>
        /// Used only in toggle mode (RequireHold = false), where the button is not being held and so
        /// PlayerController's input masking can't stop the fire for us. Same checks as
        /// PlayerController.TakeInput / InInventoryEtc.
        /// </summary>
        private static bool IsUiOpen()
        {
            return GameCamera.InFreeFly()
                || (Chat.instance != null && Chat.instance.HasFocus())
                || Menu.IsVisible()
                || global::Console.IsVisible()
                || TextInput.IsVisible()
                || Minimap.InTextInput()
                || Minimap.IsOpen()
                || InventoryGui.IsVisible()
                || StoreGui.IsVisible()
                || Hud.IsPieceSelectionVisible()
                || Hud.InRadial();
        }
    }
}
