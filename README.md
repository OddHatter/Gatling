# Oddhatter-Gatling

Formerly MachineArbalest. The plugin ID (`com.oddhatter.machinearbalest`) is unchanged, so existing settings carry over.

Turns Valheim's crossbows, bows and elemental staffs (Dundr, Staff of Embers, Staff of Frost, Staff of Fracturing)
into automatic weapons. Equip one, hold primary attack, and it keeps firing until you release, swap weapons, run out
of ammo, die, or open a menu. Each weapon type has its own on/off toggle and fire rate:
crossbows 10/s, bows 5/s, staffs about 3/s by default. Arrows always fly as if fully drawn.

Every shot goes through Valheim's own `Attack` code, so ammo use, projectile, damage, skill gain,
status effects, durability, sounds/effects, recoil and multiplayer sync stay vanilla.

Standalone. Does not depend on or modify FastArbalest; works alongside it.

## Config (`BepInEx/config/com.oddhatter.machinearbalest.cfg`)

| Setting | Default | Meaning |
|---|---|---|
| Enabled | true | Master switch. |
| FireInterval | 0.10 | Crossbows: seconds between shots (min 0.01). Evaluated once per physics step (~0.02s), so the effective cap is ~50 shots/s. |
| BowFireInterval | 0.20 | Bows: seconds between shots. |
| StaffFireInterval | 0.30 | Elemental staffs: seconds between casts. |
| RequireHold | true | true: fire while held. false: click toggles auto-fire on/off. |
| StaminaCostMultiplier | 1.0 | Stamina per shot. Auto-fire charges each shot the reload (crossbows) or full draw (bows) it skips, plus the attack. 0 = free, 0.5 = half. |
| EitrCostMultiplier | 1.0 | Eitr per shot (staffs), including any reload or draw eitr. 0 = free, 0.5 = half. |
| AmmoCostMultiplier | 1.0 | Bolts/arrows used per shot. 0.1 = one every 10 shots, 0 = never used up (you still need one in your inventory; its type still sets damage and effects). |
| DamageMultiplier | 1.0 | Projectile damage multiplier. |
| RecoilMultiplier | 1.0 | Shooter pushback multiplier. 0 = no recoil. |
| DurabilityMultiplier | 1.0 | Durability lost per shot. 0.1 offsets a 10x fire rate; 0 = no wear. |
| Crossbows | true | (Weapons) Auto-fire for crossbows. |
| Bows | true | (Weapons) Auto-fire for bows. Each arrow costs the stamina of a full draw. |
| ElementalStaffs | true | (Weapons) Auto-fire for elemental magic staffs. Blood magic staffs (summons, shields) are never affected. |
| OnlyArbalest | false | Crossbows only. false: every crossbow (Crossbows skill) is affected. true: only the vanilla Arbalest. |
| UseVanillaReload | false | Crossbows only. true: the normal reload (and FastArbalest's) paces the shots; holding still auto-fires. |
| DebugLogging | false | (Diagnostics) Logs the equipped weapon, trigger state and why each shot did or didn't fire to LogOutput.log. |

Settings apply live, with no restart needed. The mod watches its .cfg file and reloads about 0.5 s after it is saved,
whether you edit it in a text editor or r2modman's config editor. Changes made in ConfigurationManager (F1) also apply immediately.

## How it works

Based on decompiling `assembly_valheim.dll` (Valheim 1.0.21) and `FastArbalest.dll` 1.0.1.

- Vanilla crossbow cycle: `Player.PlayerAttackInput` (each physics step) → `Humanoid.StartAttack` →
  `Attack.Start` (requires the weapon to be loaded) → animation event → `Attack.OnAttackTrigger`
  (ammo, projectile, recoil, `ResetLoadedWeapon`) → a Reload *minor action* of `GetWeaponLoadingTime()`
  seconds → loaded again. Vanilla already re-fires while the button is held; the attack animation and
  the reload are what limit the rate. FastArbalest shortens only the reload (postfix on `GetWeaponLoadingTime`).
- Machine mode: a Harmony prefix on `Player.PlayerAttackInput` replaces vanilla's crossbow input handling.
  It keeps the weapon loaded and fires on a fixed schedule with `Attack.StartWithoutAnimation`, which runs the
  real `OnAttackTrigger`. It checks the same gates vanilla does (dodging, stagger, knockback, minor actions,
  stamina, eitr, ammo). There is one state block for the local player and no coroutines, so loops can't stack.
- "Held" is `Character.m_attackHold`, which Valheim already forces false while a menu, chat, console,
  inventory, map, store or radial menu has focus.
- No body animation plays in machine mode (the vanilla one is longer than one shot interval). Sounds,
  trigger/start effects and recoil still play every shot.
- Bows and staffs always use machine mode. Bow shots pass a full draw to `StartWithoutAnimation`, because accuracy,
  speed and damage scale with draw. Looping attacks (Staff of Frost) fire one cast per shot.
- Multi-volley weapons: the next shot waits until the current shot's volleys have all fired, so none are cut off.
  Stamina, eitr and health costs follow the weapon's own rules (per shot, or per volley for weapons that charge per burst).
- Costs: many weapons charge nothing per attack. Their cost is the reload drain (crossbows) or the draw drain (bows, some
  staffs), paid per second. Machine mode skips both, so each shot is charged one full reload's or draw's worth of
  stamina and eitr on top of the attack cost.

## Build

Requires the .NET SDK (8+). References the installed game and the r2modman profile's BepInEx; override paths with
`-p:ValheimDir=... -p:ProfileDir=...`.

```
dotnet build -c Release                 # → bin/Release/Gatling.dll
dotnet build -c Release -p:Deploy=true  # also copies into <profile>/BepInEx/plugins/Oddhatter-Gatling/
dotnet build -c Release -p:Package=true # → dist/Oddhatter-Gatling-<version>.zip (r2modman: Settings → Import local mod)
```

Bump the version in three places: `MachineArbalest.csproj` `<Version>`, `manifest.json`, and `MachineArbalestPlugin.Version`.
