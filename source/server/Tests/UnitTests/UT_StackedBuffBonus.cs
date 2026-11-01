using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.Effects;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
using DOL.GS.PropertyCalc;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests
{
    // #190: in a companion raid (/raid 80) two companions pulse the same chant or song (same spell ID)
    // on the player. When both pulses land in one game tick, the second child renews the first in
    // place, but the first child's queued start put it back in the effect store. Its later end then
    // subtracted the bonus a second time: endurance regeneration stuck at 0 and armor factor down 99
    // until a relog. A child that expires in the tick a second caster renews it did the same.
    //
    // Production code decides every result here: the real spell handlers, one real ECSPulseEffect per
    // caster (SpellHandler.CreateECSPulseEffect), children made by SpellHandler.ApplyEffectOnTarget (what
    // StartSpell does for each target on every pulse), StatBuffECSEffect, PlayerEffectListComponent and
    // EffectService.TickEffect over the effect store. Only the actors are test doubles.
    [TestFixture, NonParallelizable]
    public class UT_StackedBuffBonus
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private static readonly MethodInfo TickEffect = typeof(EffectService).GetMethod("TickEffect", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly PropertyInfo PulseEffectProperty = typeof(SpellHandler).GetProperty(nameof(SpellHandler.PulseEffect));

        private GameServer _previous;
        private long _time;
        private long _now;
        private double _enduranceRegenModifier;
        private PetTestLanguageScope _language;
        private readonly List<GameLiving> _actors = new();
        private readonly List<ECSGameEffect> _created = new();
        private Player _player;
        private CombatLog _log;

        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => Empty;
            protected override GS.ServerRules.IServerRules ServerRulesImpl => new GS.ServerRules.NormalServerRules();
        }

        // A companion that chants or sings.
        private sealed class Companion : GameBot
        {
            private Companion() : base((OfflineWorldBotRecord)null) { }
            public ICharacterClass Class;
            public override ICharacterClass CharacterClass => Class;
            public override byte Level => 50;
            public override bool IsAlive => true;
            public override bool IsCrowdControlled => false;
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override int Health { get; set; }
            public override int Mana { get; set; }
            public override int Endurance { get; set; }
            public override int MaxHealth => 1000;
            public override int MaxMana => 1000;
            public override int MaxEndurance => 100;
            public override int GetModified(eProperty p) => p == eProperty.SpellRange ? 100 : 0;
            public override void StartHealthRegeneration() { }
            public override void StartPowerRegeneration() { }
            public override void StartEnduranceRegeneration() { }
        }

        // The player the companions buff: a real GamePlayer, so the PlayerEffectListComponent path runs.
        private sealed class Player : GamePlayer
        {
            private static readonly ICharacterClass PaladinClass = new ClassPaladin();
            private Player() : base(null, null) { }
            public IPacketLib Packets;
            public bool Sitting;
            public override IPacketLib Out => Packets;
            public override string Name { get; set; }
            public override ICharacterClass CharacterClass => PaladinClass;
            public override bool IsAlive => true;
            public override bool IsMoving => false;
            public override bool IsAttacking => false;
            public override bool IsCasting => false;
            public override bool InCombat => false;
            public override bool IsSitting { get => Sitting; set => Sitting = value; }
            public override byte Level => 50;
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override void StartHealthRegeneration() { }
            public override void StartPowerRegeneration() { }
            public override void StartEnduranceRegeneration() { }
        }

        // Keeps the /combatstats lines ApplyBonus sends, the lines the owner saw.
        public class CombatLog : DispatchProxy
        {
            public readonly List<string> Lines = new();

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "SendMessage" && args.Length > 0 && args[0] is string s && s.StartsWith("BonusCategory"))
                    Lines.Add(s.Replace("\n", " "));

                return method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _previous = GameServer.Instance;
            _time = GameLoop.GameLoopTime;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
            _language = new PetTestLanguageScope();
            SetTime(1_000_000);
            // Tests never load server properties, so this reads 0. Use its declared default.
            _enduranceRegenModifier = GS.ServerProperties.Properties.ENDURANCE_REGEN_AMOUNT_MODIFIER;
            GS.ServerProperties.Properties.ENDURANCE_REGEN_AMOUNT_MODIFIER = 1.0;
            _player = Actor<Player>();
            _player.Name = "Edohac";
            _log = (CombatLog)(object)DispatchProxy.Create<IPacketLib, CombatLog>();
            _player.Packets = (IPacketLib)(object)_log;
            _player.UseDetailedCombatLog = true;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ECSGameEffect effect in _created)
                ServiceObjectStore.Remove(effect);

            foreach (GameLiving actor in _actors)
            {
                foreach (ECSGameEffect effect in actor.effectListComponent.GetEffects())
                    ServiceObjectStore.Remove(effect);

                ServiceObjectStore.Remove(actor.castingComponent);
                ServiceObjectStore.Remove(actor.effectListComponent);
            }

            ServiceObjectStore.UpdateAndGetAll<ECSGameEffect>(ServiceObjectType.Effect, out _);
            _actors.Clear();
            _created.Clear();
            _language.Dispose();
            SetTime(_time);
            GS.ServerProperties.Properties.ENDURANCE_REGEN_AMOUNT_MODIFIER = _enduranceRegenModifier;
            GameServer.LoadTestDouble(_previous);
        }

        // Paladin chants and Minstrel songs, as in the class line data.
        // 1084 Chant of Resilience: BaseBuff EnduranceRegenerationAmount +4 (the owner's -4 lines).
        // 1037 Crusader's Protection: a chant, so OtherBuff ArmorFactor +99 (the owner's -99 lines).
        // 1052 Body Shield: BaseBuff Resist_Body +20.
        // 1125 Song of Empowering: BaseBuff PowerRegenerationAmount +5.
        // 1118 Song of Rejuvenation: BaseBuff HealthRegenerationAmount +39.
        [TestCase("EnduranceRegenBuff", 1084, 4, 50, 5, "Paladin")]
        [TestCase("SpecArmorFactorBuff", 1037, 99, 60, 6, "Paladin")]
        [TestCase("BodyResistBuff", 1052, 20, 50, 5, "Paladin")]
        [TestCase("PowerRegenBuff", 1125, 5, 60, 6, "Minstrel")]
        [TestCase("HealthRegenBuff", 1118, 39, 60, 6, "Minstrel")]
        public void TwoCompanionsPulsingTheSameChantInOneTickLeaveNoBonusOnceBothStop(string type, int id, int value, int frequency, int duration, string casterClass)
        {
            Spell chant = Chant(type, id, value, frequency, duration);
            Companion a = NewCompanion("Ranerishelm", casterClass), b = NewCompanion("Ranenbert", casterClass);
            SpellHandler ha = NewHandler(type, a, chant), hb = NewHandler(type, b, chant);
            ECSPulseEffect pa = StartPulse(a, ha), pb = StartPulse(b, hb);
            long t0 = _now;

            // Both pulses reach the player in one game tick, before EffectListService.BeginTick.
            ECSGameSpellEffect x = Pulse(ha, pa);
            ECSGameSpellEffect y = Pulse(hb, pb);
            _player.effectListComponent.BeginTick();
            int whileBoth = Bonus(type);
            bool ghost = x.IsActive && !Listed(x) && InStore(x);

            // B keeps chanting: its next pulse renews its own child.
            SetTime(t0 + frequency * 100);
            y = Pulse(hb, pb);
            _player.effectListComponent.BeginTick();

            // A stops (a twist, a toggle, no power, out of reach). EffectService then ends A's old child.
            pa.End();
            a.effectListComponent.BeginTick();
            SetTime(t0 + duration * 1000 + 1000);
            TickEffectsInStore();
            int afterAStops = Bonus(type);
            bool bOwnsIcon = y.IsActive && ReferenceEquals(_player.effectListComponent.TryGetEffectFromEffectId(y.Icon), y);

            // B stops too (the raid disbands): nobody casts anything on the player any more.
            pb.End();
            b.effectListComponent.BeginTick();
            SetTime(_now + duration * 1000 + 1000);
            TickEffectsInStore();
            TickEffectsInStore();
            int afterBoth = Bonus(type);
            int listed = _player.effectListComponent.GetEffects().Count;
            int standing = EnduranceRegen(false), sitting = EnduranceRegen(true);

            TestContext.Out.WriteLine($"[{type} {id}] both chanting: {whileBoth}, ghost: {ghost}; A stopped: {afterAStops}, B owns icon: {bOwnsIcon}; " +
                $"both stopped: {afterBoth}, effects listed: {listed}, endurance regen standing {standing} / sitting {sitting}");
            foreach (string line in _log.Lines)
                TestContext.Out.WriteLine("  combatstats: " + line);

            Assert.Multiple(() =>
            {
                Assert.That(whileBoth, Is.EqualTo(value), "two chanters of one spell apply it once");
                Assert.That(ghost, Is.False, "the renewed child must not stay active in the effect store outside the effect list");
                Assert.That(afterAStops, Is.EqualTo(value), "A's renewed child must not take away B's live bonus");
                Assert.That(bOwnsIcon, Is.True, "B's child keeps the icon");
                Assert.That(afterBoth, Is.EqualTo(0), "once every chanter stops, no bonus is left on the player");
                Assert.That(listed, Is.EqualTo(0));
                Assert.That(standing, Is.EqualTo(1), "standing with no buff regenerates the base 1 endurance a tick");
                Assert.That(sitting, Is.EqualTo(4), "sitting with no buff regenerates the base 4 endurance a tick");
                Assert.That(_log.Lines.Count(l => l.EndsWith($"EffectiveValue: {value}")), Is.EqualTo(1), "/combatstats: the bonus is added once");
                Assert.That(_log.Lines.Count(l => l.EndsWith($"EffectiveValue: {-value}")), Is.EqualTo(1), "/combatstats: the bonus is removed once");
            });
        }

        // A stopped earlier; its last child X lingers until it expires. In the tick it expires, B's pulse
        // renews X first, then EffectService reaches X in the same tick's store snapshot and ends it.
        [TestCase("EnduranceRegenBuff", 1084, 4, 50, 5)]
        [TestCase("SpecArmorFactorBuff", 1037, 99, 60, 6)]
        public void ChildExpiringInTheTickAnotherCompanionRenewsItKeepsTheLiveBonus(string type, int id, int value, int frequency, int duration)
        {
            Spell chant = Chant(type, id, value, frequency, duration);
            Companion a = NewCompanion("Ranerishelm", "Paladin"), b = NewCompanion("Ranenbert", "Paladin");
            SpellHandler ha = NewHandler(type, a, chant), hb = NewHandler(type, b, chant);
            ECSPulseEffect pa = StartPulse(a, ha), pb = StartPulse(b, hb);

            ECSGameSpellEffect x = Pulse(ha, pa);
            _player.effectListComponent.BeginTick();
            pa.End();
            a.effectListComponent.BeginTick();
            Assert.That(Bonus(type), Is.EqualTo(value));

            SetTime(x.ExpireTick + 1);
            ECSGameSpellEffect y = Pulse(hb, pb);
            TickEffect.Invoke(null, new object[] { x });
            _player.effectListComponent.BeginTick();
            int whileB = Bonus(type);
            bool bOwnsIcon = y.IsActive && Listed(y) && ReferenceEquals(_player.effectListComponent.TryGetEffectFromEffectId(y.Icon), y);

            pb.End();
            b.effectListComponent.BeginTick();
            SetTime(_now + duration * 1000 + 1000);
            TickEffectsInStore();
            TickEffectsInStore();
            int afterBoth = Bonus(type);

            TestContext.Out.WriteLine($"[{type} {id}] B renewed the expiring child: {whileB}, B owns icon: {bOwnsIcon}; both stopped: {afterBoth}");
            foreach (string line in _log.Lines)
                TestContext.Out.WriteLine("  combatstats: " + line);

            Assert.Multiple(() =>
            {
                Assert.That(whileB, Is.EqualTo(value), "the expiring child must not take away B's live bonus");
                Assert.That(bOwnsIcon, Is.True, "B's child keeps the icon");
                Assert.That(afterBoth, Is.EqualTo(0), "once every chanter stops, no bonus is left on the player");
            });
        }

        // The raid is one group, so companions get each other's chants too: a companion gets the same
        // collision (bots use player rules for regeneration, so a leak would stall them the same way).
        [TestCase("EnduranceRegenBuff", 1084, 4, 50, 5)]
        [TestCase("SpecArmorFactorBuff", 1037, 99, 60, 6)]
        public void ACompanionBuffedByTwoChantersInOneTickKeepsNoBonusOnceBothStop(string type, int id, int value, int frequency, int duration)
        {
            Spell chant = Chant(type, id, value, frequency, duration);
            Companion a = NewCompanion("Ranerishelm", "Paladin"), b = NewCompanion("Ranenbert", "Paladin");
            Companion target = NewCompanion("Seroiselle", "Paladin");
            SpellHandler ha = NewHandler(type, a, chant), hb = NewHandler(type, b, chant);
            ECSPulseEffect pa = StartPulse(a, ha), pb = StartPulse(b, hb);
            long t0 = _now;

            Pulse(ha, pa, target);
            Pulse(hb, pb, target);
            target.effectListComponent.BeginTick();
            int whileBoth = Bonus(type, target);

            pa.End();
            a.effectListComponent.BeginTick();
            SetTime(t0 + duration * 1000 + 1000);
            TickEffectsInStore(target);
            int afterAStops = Bonus(type, target);

            pb.End();
            b.effectListComponent.BeginTick();
            SetTime(_now + duration * 1000 + 1000);
            TickEffectsInStore(target);
            TickEffectsInStore(target);
            int afterBoth = Bonus(type, target);

            TestContext.Out.WriteLine($"[{type} {id} on a companion] both chanting: {whileBoth}; A stopped: {afterAStops}; both stopped: {afterBoth}");

            Assert.Multiple(() =>
            {
                Assert.That(whileBoth, Is.EqualTo(value));
                Assert.That(afterAStops, Is.EqualTo(value), "A's renewed child must not take away B's live bonus");
                Assert.That(afterBoth, Is.EqualTo(0), "once every chanter stops, no bonus is left on the companion");
            });
        }

        private static Spell Chant(string type, int id, int value, int frequency, int duration) => new(new DbSpell
        {
            SpellID = id, Name = type, Type = type, Target = "Group", Range = 1500, Value = value,
            Pulse = 1, Frequency = frequency, Duration = duration, Icon = id, ClientEffect = id
        }, 50);

        private static SpellHandler NewHandler(string type, GameLiving caster, Spell spell) => type switch
        {
            "EnduranceRegenBuff" => new EnduranceRegenSpellHandler(caster, spell, Line(caster)),
            "SpecArmorFactorBuff" => new SpecArmorFactorBuff(caster, spell, Line(caster)),
            "BodyResistBuff" => new BodyResistBuff(caster, spell, Line(caster)),
            "PowerRegenBuff" => new PowerRegenSpellHandler(caster, spell, Line(caster)),
            "HealthRegenBuff" => new HealthRegenSpellHandler(caster, spell, Line(caster)),
            _ => throw new ArgumentException(type)
        };

        private static SpellLine Line(GameLiving caster) => caster is Companion { Class: ClassMinstrel }
            ? new SpellLine("Instruments", "Instruments", "Instruments", true)
            : new SpellLine("Chants", "Chants", "Chants", true);

        private int Bonus(string type, GameLiving target = null)
        {
            target ??= _player;
            return type switch
            {
                "EnduranceRegenBuff" => target.BaseBuffBonusCategory[eProperty.EnduranceRegenerationAmount],
                "SpecArmorFactorBuff" => target.OtherBonus[eProperty.ArmorFactor],
                "BodyResistBuff" => target.BaseBuffBonusCategory[eProperty.Resist_Body],
                "PowerRegenBuff" => target.BaseBuffBonusCategory[eProperty.PowerRegenerationAmount],
                "HealthRegenBuff" => target.BaseBuffBonusCategory[eProperty.HealthRegenerationAmount],
                _ => throw new ArgumentException(type)
            };
        }

        private int EnduranceRegen(bool sitting)
        {
            _player.Sitting = sitting;
            int regen = new EnduranceRegenerationAmountCalculator().CalcValue(_player, eProperty.EnduranceRegenerationAmount);
            _player.Sitting = false;
            return regen;
        }

        // What CastingService does when a chant is cast: the caster's pulse effect.
        private ECSPulseEffect StartPulse(Companion caster, SpellHandler handler)
        {
            ECSPulseEffect pulse = handler.CreateECSPulseEffect(caster, 1.0);
            PulseEffectProperty.SetValue(handler, pulse);
            caster.effectListComponent.BeginTick();
            _created.Add(pulse);
            Assert.That(pulse.IsActive, Is.True, "the pulse must start");
            return pulse;
        }

        // One pulse of this caster reaching the target (the player unless given).
        private ECSGameSpellEffect Pulse(SpellHandler handler, ECSPulseEffect pulse, GameLiving target = null)
        {
            target ??= _player;
            handler.ApplyEffectOnTarget(target);
            ECSGameSpellEffect child = pulse.ChildEffects[target];
            _created.Add(child);
            return child;
        }

        // One EffectService tick over the target's effects in the store, then EffectListService.BeginTick.
        private void TickEffectsInStore(GameLiving target = null)
        {
            target ??= _player;
            List<ECSGameEffect> all = ServiceObjectStore.UpdateAndGetAll<ECSGameEffect>(ServiceObjectType.Effect, out int last);
            List<ECSGameEffect> mine = new();

            for (int i = 0; i <= last; i++)
            {
                ECSGameEffect effect = all[i];

                if (effect != null && effect.ServiceObjectId.IsSet && effect.Owner == target)
                    mine.Add(effect);
            }

            foreach (ECSGameEffect effect in mine)
                TickEffect.Invoke(null, new object[] { effect });

            target.effectListComponent.BeginTick();
        }

        private static bool InStore(ECSGameEffect effect)
        {
            ServiceObjectStore.UpdateAndGetAll<ECSGameEffect>(ServiceObjectType.Effect, out _);
            return effect.ServiceObjectId.IsSet;
        }

        private bool Listed(ECSGameEffect effect) => _player.effectListComponent.GetEffects().Contains(effect);

        private void SetTime(long time)
        {
            _now = time;
            typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null, time);
        }

        private static void Field(Type type, object target, string name, object value) => type.GetField(name, Hidden).SetValue(target, value);

        private Companion NewCompanion(string name, string casterClass)
        {
            Companion companion = Actor<Companion>();
            companion.Class = casterClass == "Minstrel" ? new ClassMinstrel() : new ClassPaladin();
            companion.Name = name;
            companion.Health = 1000;
            companion.Mana = 1000;
            companion.Endurance = 100;
            return companion;
        }

        private T Actor<T>() where T : GameLiving
        {
            T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            actor.ObjectState = GameObject.eObjectState.Active;

            foreach (string mutex in new[] { "_changeHealthLock", "_changeManaLock", "_changeEnduranceLock", "_abilitiesLock" })
                Field(typeof(GameLiving), actor, mutex, new System.Threading.Lock());

            foreach (string indexer in new[] { "ItemBonus", "AbilityBonus", "BaseBuffBonusCategory", "SpecBuffBonusCategory", "OtherBonus", "DebuffCategory", "SpecDebuffCategory" })
                Field(typeof(GameLiving), actor, "<" + indexer + ">k__BackingField", new PropertyIndexer());

            Field(typeof(GameLiving), actor, "<TempProperties>k__BackingField", new PropertyCollection());
            Field(typeof(GameLiving), actor, "<BuffBonusMultCategory1>k__BackingField", new MultiplicativePropertiesHybrid());
            Field(typeof(GameLiving), actor, "m_effects", new GameEffectList(actor));
            Field(typeof(GameLiving), actor, "m_abilities", new Dictionary<string, Ability>());
            Field(typeof(GameLiving), actor, "<ActivePulseSpells>k__BackingField", new ConcurrentDictionary<eSpellType, Spell>());

            if (actor is GameNPC)
            {
                Field(typeof(GameNPC), actor, "m_brains", new ArrayList());
                Field(typeof(GameNPC), actor, "m_spells", new List<Spell>());
            }

            actor.castingComponent = CastingComponent.Create(actor);

            if (actor is GameNPC npc)
                npc.castingComponent = (NpcCastingComponent)actor.castingComponent;

            actor.effectListComponent = EffectListComponent.Create(actor);
            actor.attackComponent = new AttackComponent(actor);
            _actors.Add(actor);
            return actor;
        }
    }
}
