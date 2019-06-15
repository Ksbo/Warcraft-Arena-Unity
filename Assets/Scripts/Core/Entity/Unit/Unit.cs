﻿using System;
using System.Collections.Generic;
using System.Linq;
using Common;
using JetBrains.Annotations;
using UdpKit;
using UnityEngine;

using EventHandler = Common.EventHandler;

namespace Core
{
    public abstract class Unit : WorldEntity
    {
        public new class CreateToken : WorldEntity.CreateToken
        {
            public DeathState DeathState;
            
            public override void Read(UdpPacket packet)
            {
                base.Read(packet);

                DeathState = (DeathState)packet.ReadInt();
            }

            public override void Write(UdpPacket packet)
            {
                base.Write(packet);

                packet.WriteInt((int)DeathState);
            }

            public void Attached(Unit unit)
            {
                unit.deathState = DeathState;
            }
        }

        [SerializeField, UsedImplicitly, Header("Unit"), Space(10)]
        private CapsuleCollider unitCollider;
        [SerializeField, UsedImplicitly]
        private UnitAttributeDefinition unitAttributeDefinition;
        [SerializeField, UsedImplicitly]
        private UnitMovementDefinition unitMovementDefinition;

        private IUnitState unitState;
        private DeathState deathState;

        private CreateToken createToken;
        private EntityAttributeInt health;
        private EntityAttributeInt maxHealth;
        private EntityAttributeInt mana;
        private EntityAttributeInt maxMana;
        private EntityAttributeInt level;
        private EntityAttributeFloat modHaste;
        private EntityAttributeFloat modRangedHaste;
        private EntityAttributeFloat modSpellHaste;
        private EntityAttributeFloat modRegenHaste;
        private EntityAttributeFloat critPercentage;
        private EntityAttributeFloat rangedCritPercentage;
        private EntityAttributeFloat spellCritPercentage;

        private readonly Dictionary<int, List<Aura>> ownedAuras = new Dictionary<int, List<Aura>>();
        private readonly Dictionary<StatType, float> createStats = new Dictionary<StatType, float>();
        private readonly Dictionary<UnitMoveType, float> speedRates = new Dictionary<UnitMoveType, float>();
        private readonly List<AuraApplication> visibleAuras = new List<AuraApplication>();

        private ThreatManager ThreatManager { get; set; }
        private UnitState UnitState { get; set; }
        internal UnitAI AI { get; private set; }

        public ulong Target { get; private set; }
        public UnitFlags UnitFlags { get; private set; }
        public FactionDefinition Faction { get; private set; }
        public SpellHistory SpellHistory { get; private set; }
        public CapsuleCollider UnitCollider => unitCollider;

        public int Level => level.Value;
        public int Health => health.Value;
        public int MaxHealth => maxHealth.Value;
        public int BaseMana => mana.Base;
        public int Mana => mana.Value;
        public int MaxMana => maxMana.Value;
        public bool HasFullHealth => health.Value == maxHealth.Value;
        public float HealthRatio => maxHealth.Value > 0 ? (float)Health / MaxHealth : 0.0f;
        public float HealthPercent => 100.0f * HealthRatio;

        public float ModHaste => modHaste.Value;
        public float ModRangedHaste => modRangedHaste.Value;
        public float ModSpellHaste => modSpellHaste.Value;
        public float ModRegenHaste => modRegenHaste.Value;
        public float CritPercentage => critPercentage.Value;
        public float RangedCritPercentage => rangedCritPercentage.Value;
        public float SpellCritPercentage => spellCritPercentage.Value;

        public bool IsMovementBlocked => HasState(UnitState.Root) || HasState(UnitState.Stunned);
        public bool IsAlive => deathState == DeathState.Alive;
        public bool IsDead => deathState == DeathState.Dead || deathState == DeathState.Corpse;
        public bool IsInCombat => UnitFlags.HasTargetFlag(UnitFlags.InCombat);
        public bool IsControlledByPlayer => this is Player;
        public bool IsStopped => !HasState(UnitState.Moving);
        public bool IsFeared => HasAuraType(AuraType.ModFear);
        public bool IsFrozen => HasAuraWithMechanic(SpellMechanics.Freeze);
        public SpellResourceType PowerType => SpellResourceType.Mana;

        public bool HasSpell(int spellId) => true;
        public bool IsHostileTo(Unit unit) => Faction.HostileFactions.Contains(unit.Faction);
        public bool IsFriendlyTo(Unit unit) => Faction.FriendlyFactions.Contains(unit.Faction);
        public bool HealthBelowPercent(int percent) => health.Value < CountPercentFromMaxHealth(percent);
        public bool HealthAbovePercent(int percent) => health.Value > CountPercentFromMaxHealth(percent);
        public bool HealthAbovePercentHealed(int percent, int healAmount) => health.Value + healAmount > CountPercentFromMaxHealth(percent);
        public bool HealthBelowPercentDamaged(int percent, int damageAmount) => health.Value - damageAmount < CountPercentFromMaxHealth(percent);
        public long CountPercentFromMaxHealth(int percent) => maxHealth.Value.CalculatePercentage(percent);
        public long CountPercentFromCurrentHealth(int percent) => health.Value.CalculatePercentage(percent);
        public bool IsValidAttackTarget(Unit target, SpellInfo bySpell, WorldEntity entity = null) => true;
        public bool IsValidAssistTarget(Unit target, SpellInfo bySpell) => true;
        public float GetSpeed(UnitMoveType type) => speedRates[type] * unitMovementDefinition.BaseSpeedByType(type);
        public float GetSpeedRate(UnitMoveType type) => speedRates[type];
        public float GetPowerPercent(SpellResourceType type) => GetMaxPower(type) > 0 ? 100.0f * GetPower(type) / GetMaxPower(type) : 0.0f;
        public int GetPower(SpellResourceType type) => mana.Value;
        public int GetMaxPower(SpellResourceType type) => maxMana.Value;

        [UsedImplicitly]
        protected override void Awake()
        {
            base.Awake();

            Faction = Balance.DefaultFaction;

            health = new EntityAttributeInt(this, unitAttributeDefinition.BaseHealth, int.MaxValue, EntityAttributes.Health);
            maxHealth = new EntityAttributeInt(this, unitAttributeDefinition.BaseMaxHealth, int.MaxValue, EntityAttributes.MaxHealth);
            mana = new EntityAttributeInt(this, unitAttributeDefinition.BaseMana, int.MaxValue, EntityAttributes.Power);
            maxMana = new EntityAttributeInt(this, unitAttributeDefinition.BaseMaxMana, int.MaxValue, EntityAttributes.MaxPower);
            level = new EntityAttributeInt(this, 1, int.MaxValue, EntityAttributes.Level);
            modHaste = new EntityAttributeFloat(this, 1.0f, float.MaxValue, EntityAttributes.ModHaste);
            modRangedHaste = new EntityAttributeFloat(this, 1.0f, float.MaxValue, EntityAttributes.ModRangedHaste);
            modSpellHaste = new EntityAttributeFloat(this, 1.0f, float.MaxValue, EntityAttributes.ModSpellHaste);
            modRegenHaste = new EntityAttributeFloat(this, 1.0f, float.MaxValue, EntityAttributes.ModRegenHaste);
            critPercentage = new EntityAttributeFloat(this, unitAttributeDefinition.CritPercentage, float.MaxValue, EntityAttributes.CritPercentage);
            rangedCritPercentage = new EntityAttributeFloat(this, 1.0f, unitAttributeDefinition.RangedCritPercentage, EntityAttributes.RangedCritPercentage);
            spellCritPercentage = new EntityAttributeFloat(this, 1.0f, unitAttributeDefinition.SpellCritPercentage, EntityAttributes.SpellCritPercentage);

            SpellHistory = new SpellHistory(this);
        }

        public override void Attached()
        {
            unitState = entity.GetState<IUnitState>();

            base.Attached();

            foreach (UnitMoveType moveType in StatUtils.UnitMoveTypes)
                speedRates[moveType] = 1.0f;

            createToken = (CreateToken)entity.attachToken;
            createToken.Attached(this);

            if (!IsOwner)
            {
                unitState.AddCallback(nameof(unitState.DeathState), OnDeathStateChanged);
                unitState.AddCallback(nameof(unitState.Health), OnHealthStateChanged);
            }

            ThreatManager = new ThreatManager(this);
            MovementInfo.Attached(unitState, this);
            WorldManager.UnitManager.Attach(this);

            SetMap(WorldManager.FindMap(1));
        }

        public override void Detached()
        {
            // for instant manual client detach without waiting for Photon
            if (!IsValid)
                return;

            unitState.RemoveAllCallbacks();

            ResetMap();

            ThreatManager.Dispose();
            WorldManager.UnitManager.Detach(this);
            MovementInfo.Detached();

            unitState = null;
            createToken = null;

            base.Detached();
        }

        public abstract void Accept(IUnitVisitor unitVisitor);

        #region Attribute Handling

        internal void AddState(UnitState state) { UnitState |= state; }

        internal bool HasState(UnitState state) { return (UnitState & state) != 0; }

        internal void RemoveState(UnitState state) { UnitState &= ~state; }

        internal void SetFlag(UnitFlags flag) => UnitFlags |= flag;

        internal void RemoveFlag(UnitFlags flag) => UnitFlags &= ~flag;

        internal void AddFlag(MovementFlags f) { MovementInfo.AddMovementFlag(f); }

        internal void RemoveFlag(MovementFlags f) { MovementInfo.RemoveMovementFlag(f); }

        internal bool HasFlag(MovementFlags f) { return MovementInfo.HasMovementFlag(f); }
        
        internal int ModifyHealth(int delta)
        {
            return SetHealth(Health + delta);
        }

        internal int SetHealth(int value)
        {
            int delta = health.Set(Mathf.Clamp(value, 0, maxHealth.Value));
            unitState.Health = health.Value;
            return delta;
        }

        internal void SetPowerType(SpellResourceType spellResource) { }

        internal void SetPower(SpellResourceType spellResource, int value) { }

        internal void SetMaxPower(SpellResourceType spellResource, int value) { }

        internal int ModifyPower(SpellResourceType spellResource, int value) { return 0; }

        internal int ModifyPowerPercent(SpellResourceType spellResource, float percent, bool apply = true) { return 0; }

        internal void UpdateSpeed(UnitMoveType type)
        {
            int mainSpeedMod = 0;
            float stackBonus = 1.0f;
            float nonStackBonus = 1.0f;

            switch (type)
            {
                // only apply debuffs
                case UnitMoveType.RunBack:
                    break;
                case UnitMoveType.Run:
                    mainSpeedMod = /*GetMaxPositiveAuraModifier(SPELL_AURA_MOD_INCREASE_SPEED)*/0;
                    stackBonus = /*GetTotalAuraMultiplier(SPELL_AURA_MOD_SPEED_ALWAYS)*/0;
                    nonStackBonus += /*GetMaxPositiveAuraModifier(SPELL_AURA_MOD_SPEED_NOT_STACK) / 100.0f*/0;
                    break;
                default:
                    Debug.LogErrorFormat("Characters::UpdateSpeed: Unsupported move type - {0}", type);
                    return;
            }

            // now we ready for speed calculation
            float speed = Mathf.Max(nonStackBonus, stackBonus);
            if (mainSpeedMod != 0)
                speed *= mainSpeedMod;

            switch (type)
            {
                case UnitMoveType.Run:
                    // Normalize speed by 191 aura SPELL_AURA_USE_NORMAL_MOVEMENT_SPEED if need #TODO
                    int normalization/* = GetMaxPositiveAuraModifier(SPELL_AURA_USE_NORMAL_MOVEMENT_SPEED)*/ = 0;
                    if (normalization > 0)
                    {
                        // Use speed from aura
                        float maxSpeed = normalization / unitMovementDefinition.BaseSpeedByType(type);
                        if (speed > maxSpeed)
                            speed = maxSpeed;
                    }

                    // force minimum speed rate @ aura 437 SPELL_AURA_MOD_MINIMUM_SPEED_RATE
                    int minSpeedModRate = /*GetMaxPositiveAuraModifier(SPELL_AURA_MOD_MINIMUM_SPEED_RATE)*/0;
                    if (minSpeedModRate != 0)
                    {
                        float minSpeed = minSpeedModRate / unitMovementDefinition.BaseSpeedByType(type);
                        if (speed < minSpeed)
                            speed = minSpeed;
                    }
                    break;
            }

            // Apply strongest slow aura mod to speed
            int slow = /*GetMaxNegativeAuraModifier(SPELL_AURA_MOD_DECREASE_SPEED)*/0;
            if (slow != 0)
                speed *= slow;

            float minSpeedMod = /*(float)GetMaxPositiveAuraModifier(SPELL_AURA_MOD_MINIMUM_SPEED)*/0;
            if (minSpeedMod > 0)
            {
                float minSpeed = minSpeedMod / 100.0f;
                if (speed < minSpeed)
                    speed = minSpeed;
            }

            SetSpeedRate(type, speed);
        }

        internal void SetSpeed(UnitMoveType type, float newValue)
        {
            SetSpeedRate(type, newValue / unitMovementDefinition.BaseSpeedByType(type));
        }

        internal void SetSpeedRate(UnitMoveType type, float rate)
        {
            if (rate < 0)
                rate = 0.0f;

            speedRates[type] = rate;
        }

        internal void StopMoving() { }

        internal void SetControlled(bool apply, UnitState state) { }

        #endregion

        #region Spell Handling

        internal SpellCastResult CastSpell(SpellCastTargets targets, SpellInfo spellInfo, SpellCastFlags spellFlags = 0, AuraEffect triggeredByAura = null, ulong originalCaster = 0)
        {
            return new Spell(this, spellInfo, spellFlags, originalCaster).Prepare(targets, triggeredByAura);
        }

        internal int DamageBySpell(SpellCastDamageInfo damageInfoInfo)
        {
            if (damageInfoInfo == null)
                return 0;

            Unit victim = damageInfoInfo.Target;

            if (victim == null)
                return 0;

            if (!victim.IsAlive)
                return 0;

            SpellInfo spellProto = Balance.SpellInfosById.ContainsKey(damageInfoInfo.SpellId) ? Balance.SpellInfosById[damageInfoInfo.SpellId] : null;
            if (spellProto == null)
            {
                Debug.LogErrorFormat("Unit.DamageBySpell has wrong spellDamageInfo->SpellID: {0}", damageInfoInfo.SpellId);
                return 0;
            }

            EventHandler.ExecuteEvent(EventHandler.GlobalDispatcher, GameEvents.SpellDamageDone, this, victim, damageInfoInfo.Damage, damageInfoInfo.HitInfo == HitType.CriticalHit);

            return DealDamage(victim, damageInfoInfo.Damage);
        }

        internal int HealBySpell(Unit target, SpellInfo spellInfo, int healAmount, bool critical = false)
        {
            return DealHeal(target, healAmount);
        }

        internal int DealDamage(Unit target, int damageAmount)
        {
            if (damageAmount < 1)
                return 0;

            int healthValue = target.Health;
            if (healthValue <= damageAmount)
            {
                Kill(target);
                return healthValue;
            }

            return target.ModifyHealth(-damageAmount);
        }

        internal int DealHeal(Unit target, int healAmount)
        {
            if(healAmount < 1)
                return 0;

            return target.ModifyHealth(healAmount);
        }

        internal void Kill(Unit victim)
        {
            if (victim.Health <= 0)
                return;

            victim.SetHealth(0);
            victim.ModifyDeathState(DeathState.Dead);
        }

        internal void ModifyDeathState(DeathState newState)
        {
            if (deathState == newState)
                return;

            unitState.DeathState = (int)(createToken.DeathState = deathState = newState);
        }

        internal int CalculateSpellDamageTaken(SpellCastDamageInfo damageInfoInfo, int damage, SpellInfo spellInfo)
        {
            if (damage < 0)
                return 0;

            Unit victim = damageInfoInfo.Target;
            if (victim == null || !victim.IsAlive)
                return 0;

            SpellSchoolMask damageSchoolMask = damageInfoInfo.SchoolMask;

            if (damage > 0)
            {
                int absorb = damageInfoInfo.Absorb;
                int resist = damageInfoInfo.Resist;
                CalcAbsorbResist(victim, damageSchoolMask, SpellDamageType.Direct, damage, ref absorb, ref resist, spellInfo);
                damageInfoInfo.Absorb = absorb;
                damageInfoInfo.Resist = resist;
                damage -= damageInfoInfo.Absorb + damageInfoInfo.Resist;
            }
            else
                damage = 0;

            return damageInfoInfo.Damage = damage;
        }
        
        internal SpellMissType SpellHitResult(Unit victim, SpellInfo spellInfo, bool canReflect = false)
        {
            // Check for immune
            /*if (victim->IsImmunedToSpell(spellInfo))
                return SPELL_MISS_IMMUNE;*/

            // All positive spells can`t miss
            if (spellInfo.IsPositive() && !IsHostileTo(victim)) // prevent from affecting enemy by "positive" spell
                return SpellMissType.None;

            // Check for immune
            /*if (victim->IsImmunedToDamage(spellInfo))
                return SPELL_MISS_IMMUNE;*/

            if (this == victim)
                return SpellMissType.None;

            // Try victim reflect spell
            /*if (CanReflect)
            {
                int32 reflectchance = victim->GetTotalAuraModifier(SPELL_AURA_REFLECT_SPELLS);
                    Unit::AuraEffectList const& mReflectSpellsSchool = victim->GetAuraEffectsByType(SPELL_AURA_REFLECT_SPELLS_SCHOOL);
                for (Unit::AuraEffectList::const_iterator i = mReflectSpellsSchool.begin(); i != mReflectSpellsSchool.end(); ++i)
                    if ((*i)->GetMiscValue() & spellInfo->GetSchoolMask())
                        reflectchance += (*i)->GetAmount();
                if (reflectchance > 0 && roll_chance_i(reflectchance))
                {
                    // Start triggers for remove charges if need (trigger only for victim, and mark as active spell)
                    ProcDamageAndSpell(victim, PROC_FLAG_NONE, PROC_FLAG_TAKEN_SPELL_MAGIC_DMG_CLASS_NEG, PROC_EX_REFLECT, 1, BASE_ATTACK, spellInfo);
                    return SPELL_MISS_REFLECT;
                }
            }*/

            /*switch (spellInfo->DmgClass)
            {
                case SPELL_DAMAGE_CLASS_RANGED:
                case SPELL_DAMAGE_CLASS_MELEE:
                    return MeleeSpellHitResult(victim, spellInfo);
                case SPELL_DAMAGE_CLASS_NONE:
                    return SPELL_MISS_NONE;
                case SPELL_DAMAGE_CLASS_MAGIC:
                    return MagicSpellHitResult(victim, spellInfo);
            }*/
            return SpellMissType.None;
        }

        internal float GetSpellMinRangeForTarget(Unit target, SpellInfo spellInfo)
        {
            if (Mathf.Approximately(spellInfo.MinRangeFriend, spellInfo.MinRangeHostile))
                return spellInfo.GetMinRange(false);
            if (target == null)
                return spellInfo.GetMinRange(true);
            return spellInfo.GetMinRange(!IsHostileTo(target));
        }

        internal float GetSpellMaxRangeForTarget(Unit target, SpellInfo spellInfo)
        {
            if (Mathf.Approximately(spellInfo.MaxRangeFriend, spellInfo.MaxRangeHostile))
                return spellInfo.GetMaxRange(false);
            if (target == null)
                return spellInfo.GetMaxRange(true);
            return spellInfo.GetMaxRange(!IsHostileTo(target));
        }

        internal void ModifyAuraState(AuraStateType flag, bool apply) { }

        internal bool HasAuraState(AuraStateType flag, SpellInfo spellProto = null, Unit caster = null) { return false; }

        internal Unit GetMagicHitRedirectTarget(Unit victim, SpellInfo spellProto) { return null; }

        internal Unit GetMeleeHitRedirectTarget(Unit victim, SpellInfo spellProto = null) { return null; }

        internal int SpellBaseDamageBonusDone(SpellSchoolMask schoolMask) { return 0; }

        internal int SpellBaseDamageBonusTaken(SpellSchoolMask schoolMask) { return 0; }

        internal int SpellDamageBonusDone(Unit victim, SpellInfo spellProto, int damage, SpellDamageType damagetype, SpellEffectInfo effect, uint stack = 1) { return 0; }

        internal float SpellDamagePctDone(Unit victim, SpellInfo spellProto, SpellDamageType damagetype) { return 0.0f; }

        internal void ApplySpellMod(SpellInfo spellInfo, SpellModifierType modifierType, ref int value) { }

        internal void ApplySpellMod(SpellInfo spellInfo, SpellModifierType modifierType, ref float value) { }

        internal int SpellDamageBonusTaken(Unit caster, SpellInfo spellProto, int damage, SpellDamageType damagetype, SpellEffectInfo effect, uint stack = 1)
        {
            return damage;
        }

        internal int SpellBaseHealingBonusDone(SpellSchoolMask schoolMask) { return 0; }

        internal int SpellBaseHealingBonusTaken(SpellSchoolMask schoolMask) { return 0; }

        internal uint SpellHealingBonusDone(Unit victim, SpellInfo spellProto, uint healamount, SpellDamageType damagetype, SpellEffectInfo effect, uint stack = 1) { return 0; }

        internal uint SpellHealingBonusTaken(Unit caster, SpellInfo spellProto, uint healamount, SpellDamageType damagetype, SpellEffectInfo effect, uint stack = 1) { return 0; }

        internal float SpellHealingPercentDone(Unit victim, SpellInfo spellProto) { return 0.0f; }

        internal bool IsSpellBlocked(Unit victim, SpellInfo spellProto, WeaponAttackType attackType = WeaponAttackType.BaseAttack) { return false; }

        internal bool IsSpellCrit(Unit victim, SpellInfo spellProto, SpellSchoolMask schoolMask, WeaponAttackType attackType = WeaponAttackType.BaseAttack) { return false; }

        internal float GetUnitSpellCriticalChance(Unit victim, SpellInfo spellProto, SpellSchoolMask schoolMask, WeaponAttackType attackType = WeaponAttackType.BaseAttack) { return 0.0f; }

        internal int SpellCriticalHealingBonus(SpellInfo spellProto, int damage, Unit victim) { return 0; }

        internal void ApplySpellDispelImmunity(SpellInfo spellProto, SpellDispelType spellDispelType, bool apply) { }

        internal bool IsImmunedToDamage(SpellSchoolMask meleeSchoolMask) { return false; }

        internal bool IsImmunedToDamage(SpellInfo spellProto) { return false; }

        internal bool IsImmunedToSpellEffect(SpellInfo spellProto, int index) { return false; }

        internal uint CalcSpellResistance(Unit victim, SpellSchoolMask schoolMask, SpellInfo spellProto) { return 0; }

        internal void CalcAbsorbResist(Unit victim, SpellSchoolMask schoolMask, SpellDamageType damagetype, int damage, ref int absorb, ref int resist, SpellInfo spellProto = null) { }

        internal void CalcHealAbsorb(Unit victim, SpellInfo spellProto, ref int healAmount, ref int absorb) { }

        internal float ApplyEffectModifiers(SpellInfo spellProto, int effectIndex, float value)
        {
            //var modOwner = this;
            /*modOwner->ApplySpellMod(spellProto->Id, SPELLMOD_ALL_EFFECTS, value);
            switch (effect_index)
            {
                case 0:
                    modOwner->ApplySpellMod(spellProto->Id, SPELLMOD_EFFECT1, value);
                    break;
                case 1:
                    modOwner->ApplySpellMod(spellProto->Id, SPELLMOD_EFFECT2, value);
                    break;
                case 2:
                    modOwner->ApplySpellMod(spellProto->Id, SPELLMOD_EFFECT3, value);
                    break;
                case 3:
                    modOwner->ApplySpellMod(spellProto->Id, SPELLMOD_EFFECT4, value);
                    break;
                case 4:
                    modOwner->ApplySpellMod(spellProto->Id, SPELLMOD_EFFECT5, value);
                    break;
            }*/
            return value;
        }

        internal int CalcSpellDuration(SpellInfo spellProto) { return 0; }

        internal int ModSpellDuration(SpellInfo spellProto, Unit target, int duration, bool positive, uint effectMask) { return 0; }

        internal void ModSpellCastTime(SpellInfo spellProto, ref int castTime, Spell spell = null) { }

        internal void ModSpellDurationTime(SpellInfo spellProto, ref int castTime, Spell spell = null) { }

        #endregion

        #region Aura Handling

        internal Aura TryStackingOrRefreshingExistingAura(SpellInfo newAuraSpellInfo, ulong originalCasterId, ulong targetCasterId, List<int> baseAmount = null)
        {
            Assert.IsTrue(originalCasterId != 0 || targetCasterId != 0);

            // check if these can stack anyway
            if (originalCasterId == 0 && !newAuraSpellInfo.IsStackableOnOneSlotWithDifferentCasters())
                originalCasterId = targetCasterId;

            // find current aura from spell and change it's stack amount, or refresh it's duration
            var foundAura = FindOwnedAura(newAuraSpellInfo.Id, originalCasterId);
            if (foundAura == null)
                return null;

            // update basepoints with new values - effect amount will be recalculated in ModStackAmount
            foreach (var spellEffectInfo in foundAura.GetSpellEffectInfos())
            {
                if (spellEffectInfo == null)
                    continue;

                AuraEffect auraEffect = foundAura.GetEffect(spellEffectInfo.Index);
                if (auraEffect == null)
                    continue;

                int newBasePoints = baseAmount?[spellEffectInfo.Index] ?? spellEffectInfo.BasePoints;
                foundAura.GetEffect(spellEffectInfo.Index).UpdateBaseAmount(newBasePoints);
            }

            // try to increase stack amount
            foundAura.ModStackAmount(1);
            return foundAura;
        }

        internal Aura FindOwnedAura(int spellId, ulong casterId, Aura exceptAura = null)
        {
            if (!ownedAuras.ContainsKey(spellId))
                return null;

            return ownedAuras[spellId].Find(sameSpellAura => sameSpellAura.CasterId == casterId && exceptAura != sameSpellAura);
        }

        internal void AddAura(UnitAura aura, Unit caster)
        {
            if (!ownedAuras.ContainsKey(aura.SpellInfo.Id))
                ownedAuras[aura.SpellInfo.Id] = new List<Aura>();

            ownedAuras[aura.SpellInfo.Id].Add(aura);
        }

        internal AuraApplication CreateAuraApplication(Aura aura, uint effMask) { return null; }

        internal void ApplyAuraEffect(Aura aura, byte effIndex) { }

        internal void ApplyAura(AuraApplication application, uint effMask) { }

        internal void UnapplyAura(AuraRemoveMode removeMode) { }

        internal void UnapplyAura(AuraApplication application, AuraRemoveMode removeMode) { }

        internal void RegisterAuraEffect(AuraEffect auraEffect, bool apply) { }

        internal void RemoveOwnedAura(AuraRemoveMode removeMode = AuraRemoveMode.Default) { }

        internal void RemoveOwnedAura(uint spellId, AuraRemoveMode removeMode = AuraRemoveMode.Default) { }

        internal void RemoveOwnedAura(Aura aura, AuraRemoveMode removeMode = AuraRemoveMode.Default) { }

        internal void RemoveAura(AuraRemoveMode mode = AuraRemoveMode.Default) { }

        internal void RemoveAura(AuraApplication application, AuraRemoveMode mode = AuraRemoveMode.Default) { }

        internal void RemoveAura(Aura aura, AuraRemoveMode mode = AuraRemoveMode.Default) { }

        internal void RemoveAppliedAuras(Predicate<AuraApplication> check) { }

        internal void RemoveOwnedAuras(Predicate<Aura> check) { }

        internal void RemoveAppliedAuras(uint spellId, Predicate<AuraApplication> check) { }

        internal void RemoveOwnedAuras(uint spellId, Predicate<Aura> check) { }

        internal void RemoveAurasByType(AuraType auraType, Predicate<AuraApplication> check) { }

        internal void RemoveAurasByType(AuraType auraType, bool negative = true, bool positive = true) { }

        internal void RemoveMovementImpairingAuras() { }

        internal bool HasAura(int spellId) { return false; }

        internal bool HasAuraEffect(int spellId) { return false; }

        internal bool HasAuraType(AuraType auraType) { return false; }

        internal bool HasAuraWithMechanic(SpellMechanics mechanic) { return false; }

        #endregion

        private void OnDeathStateChanged()
        {
            deathState = (DeathState)unitState.DeathState;
        }

        private void OnHealthStateChanged()
        {
            SetHealth(unitState.Health);
        }
    }
}