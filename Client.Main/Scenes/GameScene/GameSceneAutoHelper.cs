#nullable enable
using System;
using System.Collections.Generic;
using Client.Data.ATT;
using Client.Main.Configuration;
using Client.Main.Controls;
using Client.Main.Controls.UI.Game.Hud;
using Client.Main.Core.Models;
using Client.Main.Core.Utilities;
using Client.Main.Models;
using Client.Main.Objects;
using Microsoft.Extensions.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Client.Main.Scenes
{
    /// <summary>
    /// Personal server helper, visible in the client (unlike the server's offline leveling).
    /// <para>Auto farm (Home): the hero stays on the tile where it was turned on, casts the selected skill at the
    /// nearest monster in range (like holding the right mouse button), walks back to that tile when pushed away
    /// for a while and drinks the potions of the Q (health) and W (mana) slots below the thresholds.</para>
    /// <para>Auto pickup (End): zen, jewels and boxes on the ground nearby; also during normal play,
    /// but only while the hero stands still, so it doesn't fight the player's own walking.</para>
    /// </summary>
    internal sealed class GameSceneAutoHelper
    {
        private const int HealthPotionSlot = 0; // Q
        private const int ManaPotionSlot = 1;   // W
        private const double PotionIntervalMs = 1000;
        private const double PickupIntervalMs = 250;
        private const double ReturnAfterPickupMs = 500;
        private const float AnchorTolerance = 1.5f;
        private const float PickupDistance = 2f; // the server allows 3 tiles, the client position may lag a tile
        private const uint DefaultTargetRange = 4;
        private const int MaxPickupAttempts = 3;
        private const double SkipItemMs = 30_000;

        private readonly GameScene _scene;
        private readonly ILogger? _logger;
        private readonly Dictionary<ushort, (int Attempts, double SkipUntilMs)> _pickupAttempts = new();

        private Vector2 _anchor;
        private ushort _anchorMapId;
        private double _awayFromAnchorSinceMs = -1;
        private bool _walkedForPickup;
        private double _nextHealthPotionMs;
        private double _nextManaPotionMs;
        private double _nextPickupMs;
        private bool _missingPotionReported;
        private ushort? _pickupTargetId;

        public GameSceneAutoHelper(GameScene scene, ILogger? logger)
        {
            _scene = scene;
            _logger = logger;
        }

        public bool FarmEnabled { get; private set; }

        public bool PickupEnabled { get; private set; }

        private static AutoHelperSettings Settings => MuGame.AppSettings?.AutoHelper ?? new AutoHelperSettings();

        public void ToggleFarm()
        {
            var hero = _scene.Hero;
            if (FarmEnabled || hero == null || hero.IsDead)
            {
                StopFarm(FarmEnabled ? "Auto farm: OFF" : null);
                return;
            }

            FarmEnabled = true;
            _anchor = new Vector2((int)hero.Location.X, (int)hero.Location.Y);
            _anchorMapId = CurrentMapId();
            _awayFromAnchorSinceMs = -1;
            _missingPotionReported = false;
            var s = Settings;
            Say($"Auto farm: ON at ({_anchor.X}, {_anchor.Y}). Potions: Q below {s.HealthPotionPercent}% HP, W below {s.ManaPotionPercent}% MP.");
        }

        public void TogglePickup()
        {
            PickupEnabled = !PickupEnabled;
            _pickupTargetId = null;
            _pickupAttempts.Clear();
            Say(PickupEnabled ? "Auto pickup: ON (zen, jewels, boxes)" : "Auto pickup: OFF");
        }

        public void Update(GameTime gameTime)
        {
            if (!FarmEnabled && !PickupEnabled)
                return;

            var hero = _scene.Hero;
            var world = _scene.World as WalkableWorldControl;
            if (hero == null || world == null)
                return;

            double now = gameTime.TotalGameTime.TotalMilliseconds;

            if (FarmEnabled)
            {
                if (hero.IsDead)
                {
                    StopFarm("Auto farm: OFF (the character died)");
                }
                else if (CurrentMapId() != _anchorMapId)
                {
                    StopFarm("Auto farm: OFF (the map changed)");
                }
                else
                {
                    DrinkPotions(now);
                }
            }

            if (hero.IsDead)
                return;

            if (PickupEnabled && UpdatePickup(hero, now))
                return; // walking to or picking up an item

            if (FarmEnabled)
                UpdateFarm(hero, world, now);
        }

        private void UpdateFarm(Objects.Player.PlayerObject hero, WalkableWorldControl world, double now)
        {
            if (hero.IsMoving || hero.MovementIntent)
                return;

            if (Vector2.Distance(hero.Location, _anchor) > AnchorTolerance)
            {
                if (_awayFromAnchorSinceMs < 0)
                    _awayFromAnchorSinceMs = now;

                double delay = _walkedForPickup ? ReturnAfterPickupMs : Settings.ReturnDelaySeconds * 1000.0;
                if (now - _awayFromAnchorSinceMs >= delay)
                {
                    hero.MoveTo(_anchor, sendToServer: true, usePathfinding: true);
                    _awayFromAnchorSinceMs = -1;
                    _walkedForPickup = false;
                    return;
                }
            }
            else
            {
                _awayFromAnchorSinceMs = -1;
                _walkedForPickup = false;
            }

            var skill = _scene.ModernHud?.SelectedSkill;
            var skillController = _scene.SkillController;
            if (skill == null || skillController == null || hero.IsAttackOrSkillAnimationPlaying())
                return;

            var flags = world.Terrain.RequestTerrainFlag((int)hero.Location.X, (int)hero.Location.Y);
            if (flags.HasFlag(TWFlags.SafeZone))
                return;

            uint range = SkillDatabase.GetSkillRange(skill.SkillId);
            var target = FindNearestMonster(world, hero.Location, range == 0 ? DefaultTargetRange : range);
            if (target != null)
                skillController.CastSkillFromHotbar(skill, target);
        }

        private void DrinkPotions(double now)
        {
            var state = MuGame.Network?.GetCharacterState();
            var hud = _scene.ModernHud;
            if (state == null || hud == null)
                return;

            var s = Settings;
            if (now >= _nextHealthPotionMs && state.MaximumHealth > 0 &&
                state.CurrentHealth * 100 < state.MaximumHealth * (uint)s.HealthPotionPercent)
            {
                _nextHealthPotionMs = now + PotionIntervalMs;
                if (!hud.TryConsumePotionInSlot(HealthPotionSlot))
                    ReportMissingPotion("Q");
            }

            if (now >= _nextManaPotionMs && state.MaximumMana > 0 &&
                state.CurrentMana * 100 < state.MaximumMana * (uint)s.ManaPotionPercent)
            {
                _nextManaPotionMs = now + PotionIntervalMs;
                if (!hud.TryConsumePotionInSlot(ManaPotionSlot))
                    ReportMissingPotion("W");
            }
        }

        private void ReportMissingPotion(string key)
        {
            if (_missingPotionReported)
                return;

            _missingPotionReported = true;
            Say($"Auto farm: no potion for the {key} slot in the inventory.", MessageType.Error);
        }

        /// <returns><c>true</c>, while the helper walks to an item or just sent a pickup request.</returns>
        private bool UpdatePickup(Objects.Player.PlayerObject hero, double now)
        {
            if (now < _nextPickupMs)
                return _pickupTargetId != null;

            _nextPickupMs = now + PickupIntervalMs;

            // Don't take the hero from the player: only while standing (or walking to our own target).
            if (_pickupTargetId == null && (hero.IsMoving || hero.MovementIntent || hero.IsAttackOrSkillAnimationPlaying()))
                return false;

            var network = MuGame.Network;
            var scopeManager = network?.GetScopeManager();
            var characterState = network?.GetCharacterState();
            var characterService = network?.GetCharacterService();
            if (scopeManager == null || characterState == null || characterService == null)
                return false;

            float radius = Settings.PickupRadius;
            ScopeObject? best = null;
            float bestDistance = float.MaxValue;
            foreach (var type in new[] { ScopeObjectType.Money, ScopeObjectType.Item })
            {
                foreach (var obj in scopeManager.GetScopeItems(type))
                {
                    if ((obj.PositionX == 0 && obj.PositionY == 0) || !IsWanted(obj))
                        continue;

                    if (_pickupAttempts.TryGetValue(obj.Id, out var attempts) && attempts.SkipUntilMs > now)
                        continue;

                    var location = new Vector2(obj.PositionX, obj.PositionY);
                    if (FarmEnabled && Vector2.Distance(location, _anchor) > radius)
                        continue;

                    float distance = Vector2.Distance(location, hero.Location);
                    if (distance <= radius && distance < bestDistance)
                    {
                        best = obj;
                        bestDistance = distance;
                    }
                }
            }

            if (best == null)
            {
                _pickupTargetId = null;
                return false;
            }

            if (bestDistance > PickupDistance)
            {
                if (_pickupTargetId == best.Id && !hero.IsMoving && !hero.MovementIntent)
                    CountAttempt(best.Id, now); // arrived but still too far: unreachable tile

                if (!hero.IsMoving)
                {
                    _pickupTargetId = best.Id;
                    _walkedForPickup = true;
                    hero.MoveTo(new Vector2(best.PositionX, best.PositionY), sendToServer: true, usePathfinding: true);
                }

                return true;
            }

            if (hero.IsMoving)
                return true;

            CountAttempt(best.Id, now);
            _pickupTargetId = best.Id;
            characterState.SetPendingPickupRawId(best.RawId);
            if (best is ItemScopeObject itemScope)
                characterState.StashPickedItem(itemScope.ItemData.ToArray());

            ushort rawId = best.RawId;
            var version = network!.TargetVersion;
            _ = Task.Run(async () =>
            {
                try
                {
                    await characterService.SendPickupItemRequestAsync(rawId, version);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Auto pickup request for {RawId:X4} failed", rawId);
                }
            });
            return true;
        }

        private void CountAttempt(ushort id, double now)
        {
            _pickupAttempts.TryGetValue(id, out var attempts);
            int count = attempts.Attempts + 1;
            _pickupAttempts[id] = count >= MaxPickupAttempts ? (0, now + SkipItemMs) : (count, 0);
            if (count >= MaxPickupAttempts)
                _pickupTargetId = null;
        }

        /// <summary>Zen, jewels and boxes (also the boxes of Kundun, which are Box of Luck levels).</summary>
        private static bool IsWanted(ScopeObject obj)
        {
            if (obj is MoneyScopeObject)
                return true;

            if (obj is not ItemScopeObject item)
                return false;

            var definition = ItemDatabase.GetItemDefinition(item.ItemData.Span);
            if (definition == null || (definition.Group != 12 && definition.Group != 14) || definition.Name == null)
                return false;

            return definition.Name.Contains("Jewel", StringComparison.OrdinalIgnoreCase)
                || definition.Name.Contains("Gemstone", StringComparison.OrdinalIgnoreCase)
                || definition.Name.Contains("Box", StringComparison.OrdinalIgnoreCase);
        }

        private static MonsterObject? FindNearestMonster(WalkableWorldControl world, Vector2 from, uint range)
        {
            MonsterObject? nearest = null;
            float best = range + 0.5f;
            var monsters = world.Monsters;
            for (int i = 0; i < monsters.Count; i++)
            {
                var monster = monsters[i];
                if (monster == null || monster.IsDead || monster.IsSummoned || monster.World != world)
                    continue;

                float distance = Vector2.Distance(from, monster.Location);
                if (distance <= best)
                {
                    best = distance;
                    nearest = monster;
                }
            }

            return nearest;
        }

        private void StopFarm(string? message)
        {
            FarmEnabled = false;
            _awayFromAnchorSinceMs = -1;
            _walkedForPickup = false;
            if (message != null)
                Say(message);
        }

        private static ushort CurrentMapId() => MuGame.Network?.GetCharacterState()?.MapId ?? 0;

        private void Say(string text, MessageType type = MessageType.System)
        {
            _scene.ChatLog?.AddMessage("System", text, type);
        }
    }
}
