using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DXVision;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// O(N) FNV-1a 64-bit state checksum, order-independent over DXGame.Current.Entities.
    /// v2 adds projectile, production-queue and reservation sub-checksums so combat and economy
    /// divergence is detected, not just unit movement and HP.
    ///
    /// IMPORTANT: the game's ZXEntity / CCommandable / ZXLevelState classes are internal, so this
    /// hasher only touches the public DXEntity surface directly and reads ZX-specific data through
    /// reflection helpers on GameBridge. Never cast an entity to ZXEntity here (TypeAccessException).
    /// </summary>
    public static class StateHasher
    {
        public const int HashVersion = 3;
        public const int BucketCount = 16;
        public const int EpochTicks = 60;

        private static PropertyInfo[] _reservationProps;
        private static Type _reservationStateType;
        private static bool _reflectionWarned;

        private const byte FlagIgnore = 1;
        private const byte FlagProjectile = 2;
        private const byte FlagZombie = 4;
        private static readonly Dictionary<Type, byte> _entityTypeFlags = new Dictionary<Type, byte>();

        public struct EntityPosSample
        {
            public ulong Id;
            public float X;
            public float Y;
            public int Life;
            public bool IsDynamic;
        }

        public struct LevelStateSnapshotDigest
        {
            public int SimTick;
            public int GameTimeMs;
            public int Gold, Wood, Stone, Iron, Oil;
            public int EntityCount;
            public long PosChecksum, CoarsePosChecksum, LifeChecksum, ZombieAggroChecksum, ProjectileChecksum, ProductionChecksum, ReservationChecksum;
            public int ProjectileCount, QueuedOrderCount, AggroZombieCount;
            public ulong[] PosBuckets;
            public ulong[] LifeBuckets;
            public ulong[] EntityBuckets;
            public ulong HardHash;
            public ulong FullHash;

            public override string ToString()
            {
                return $"Tick={SimTick}, Time={GameTimeMs}ms, Res=({Gold}G/{Wood}W/{Stone}S/{Iron}I/{Oil}O), Entities={EntityCount}, " +
                       $"Aggro={AggroZombieCount}, Proj={ProjectileCount}, Queued={QueuedOrderCount}, Hard=0x{HardHash:X16}, Hash=0x{FullHash:X16}";
            }
        }

        private sealed class DigestAccumulators
        {
            public int EntityCount;
            public int ProjectileCount;
            public int QueuedOrderCount;
            public int AggroZombieCount;
            public long PosChecksum;
            public long CoarsePosChecksum;
            public long LifeChecksum;
            public long ZombieAggroChecksum;
            public long ProjectileChecksum;
            public long ProductionChecksum;
            public ulong[] PosBuckets;
            public ulong[] LifeBuckets;
            public ulong[] EntityBuckets;
            public List<EntityPosSample> DynamicPositions;

            public void Reset(bool allocateBuckets, List<EntityPosSample> dynamicOut)
            {
                EntityCount = 0;
                ProjectileCount = 0;
                QueuedOrderCount = 0;
                AggroZombieCount = 0;
                PosChecksum = 0;
                CoarsePosChecksum = 0;
                LifeChecksum = 0;
                ZombieAggroChecksum = 0;
                ProjectileChecksum = 0;
                ProductionChecksum = 0;
                PosBuckets = allocateBuckets ? new ulong[BucketCount] : null;
                LifeBuckets = allocateBuckets ? new ulong[BucketCount] : null;
                EntityBuckets = allocateBuckets ? new ulong[BucketCount] : null;
                DynamicPositions = dynamicOut;
            }
        }

        /// <summary>
        /// Splits the per-epoch entity hashing, reflection inspection and position sampling evenly
        /// across 60 consecutive simulation ticks (1/60 of entities per tick by ID % 60), so Host
        /// and Client eliminate periodic 60-tick CPU spikes while remaining lockstep-identical.
        /// </summary>
        public sealed class IncrementalEpochHasher
        {
            private readonly DigestAccumulators _acc = new DigestAccumulators();
            private int _lastSteppedTick = -1;
            private bool _inEpoch;

            public void Reset()
            {
                _inEpoch = false;
                _lastSteppedTick = -1;
                _acc.Reset(allocateBuckets: false, dynamicOut: null);
            }

            public bool Step(int curTick, out LevelStateSnapshotDigest digest, out List<EntityPosSample> dynamicPositions)
            {
                digest = default;
                dynamicPositions = null;

                var game = DXGame.Current;
                object ls = GameBridge.CurrentLevelState;
                if (game == null || ls == null || curTick <= 0 || curTick == _lastSteppedTick)
                    return false;

                if (_inEpoch && curTick != _lastSteppedTick + 1)
                    _inEpoch = false;

                _lastSteppedTick = curTick;
                int slot = (curTick - 1) % EpochTicks;

                if (!_inEpoch)
                {
                    if (slot != 0)
                        return false;
                    _acc.Reset(allocateBuckets: true, dynamicOut: new List<EntityPosSample>(2048));
                    _inEpoch = true;
                }

                ulong targetSlot = (ulong)slot;
                var fastEntities = GameBridge.GetEntitiesList(game);
                if (fastEntities != null)
                {
                    int n = fastEntities.Count;
                    for (int i = 0; i < n; i++)
                    {
                        var entity = fastEntities[i];
                        if (entity == null || entity.Disposed)
                            continue;
                        if ((entity.ID & 0x8000000000000000UL) != 0)
                            continue;
                        if ((entity.ID % (ulong)EpochTicks) != targetSlot)
                            continue;
                        AccumulateEntity(entity, _acc);
                    }
                }
                else if (game.Entities != null)
                {
                    foreach (var entity in game.Entities)
                    {
                        if (entity == null || entity.Disposed)
                            continue;
                        if ((entity.ID & 0x8000000000000000UL) != 0)
                            continue;
                        if ((entity.ID % (ulong)EpochTicks) != targetSlot)
                            continue;
                        AccumulateEntity(entity, _acc);
                    }
                }

                if (slot != EpochTicks - 1)
                    return false;

                _inEpoch = false;
                digest = FinalizeDigest(game, ls, curTick, _acc);
                dynamicPositions = _acc.DynamicPositions;
                _acc.DynamicPositions = null;
                _acc.PosBuckets = null;
                return true;
            }
        }

        public static LevelStateSnapshotDigest ComputeDigest(List<EntityPosSample> outDynamicPositions = null)
        {
            var game = DXGame.Current;
            object ls = GameBridge.CurrentLevelState;
            if (game == null || ls == null)
                return new LevelStateSnapshotDigest();

            var acc = new DigestAccumulators();
            acc.Reset(allocateBuckets: true, dynamicOut: outDynamicPositions);

            if (game.Entities != null)
            {
                foreach (var entity in game.Entities)
                {
                    if (entity == null || entity.Disposed)
                        continue;
                    if ((entity.ID & 0x8000000000000000UL) != 0)
                        continue;
                    AccumulateEntity(entity, acc);
                }
            }

            return FinalizeDigest(game, ls, game.IAIterationInt, acc);
        }

        private static void AccumulateEntity(DXEntity entity, DigestAccumulators acc)
        {
            // Bare DXEntity instances (e.g. blood/hit sprites) and non-interactive ambient birds
            // (Raven) are purely visual/ambient and do not affect simulation state.
            Type entityType = entity.GetType();
            if (!_entityTypeFlags.TryGetValue(entityType, out byte flags))
            {
                string typeName = entityType.Name;
                if (entityType == typeof(DXEntity) || string.Equals(typeName, "Raven", StringComparison.Ordinal))
                    flags = FlagIgnore;
                else if (IsProjectileName(typeName))
                    flags = FlagProjectile;
                else if (GameBridge.IsZombieType(entityType))
                    flags = FlagZombie;
                _entityTypeFlags[entityType] = flags;
            }
            if ((flags & FlagIgnore) != 0)
                return;

            float px = entity.Position.X;
            float py = entity.Position.Y;
            long qx = (long)Math.Round(px * 100f);
            long qy = (long)Math.Round(py * 100f);

            int lifeValue = GameBridge.GetLifeValue(entity);

            if ((flags & FlagProjectile) != 0)
            {
                acc.ProjectileCount++;
                acc.ProjectileChecksum += (long)entity.ID ^ (qx * 92837111L) ^ (qy * 689287499L) ^ (lifeValue * 283923481L);
                return;
            }

            // Projectiles are transient combat effects and tracked strictly via ProjectileCount /
            // ProjectileChecksum above so a +-1 arrow flight never triggers EntityCount divergence.
            acc.EntityCount++;

            bool isDynamic = entity.PhysicalType == DXBodyType.Dynamic;
            // Dynamic bodies carry position + HP for async alignment; static entities with HP
            // (walls/buildings) are sampled too so host-authoritative HP correction covers them.
            if (acc.DynamicPositions != null && (isDynamic || lifeValue > 0))
                acc.DynamicPositions.Add(new EntityPosSample { Id = entity.ID, X = px, Y = py, Life = lifeValue, IsDynamic = isDynamic });

            int bucketIdx = (int)(entity.ID & (ulong)(BucketCount - 1));
            long posMix = (long)entity.ID ^ (qx * 73856093L) ^ (qy * 19349663L);
            acc.PosChecksum += posMix;
            if (acc.PosBuckets != null)
                acc.PosBuckets[bucketIdx] += unchecked((ulong)posMix);

            long cx = (long)Math.Floor(px * 0.5f);
            long cy = (long)Math.Floor(py * 0.5f);
            acc.CoarsePosChecksum += (long)entity.ID ^ (cx * 73856093L) ^ (cy * 19349663L);
            long lifeMix = (long)entity.ID ^ (lifeValue * 83492791L);
            acc.LifeChecksum += lifeMix;
            if (acc.LifeBuckets != null)
                acc.LifeBuckets[bucketIdx] += unchecked((ulong)lifeMix);
            if (acc.EntityBuckets != null)
                acc.EntityBuckets[bucketIdx] += unchecked((ulong)((long)entity.ID * 2654435761L) ^ 0x9E3779B97F4A7C15UL);

            if (isDynamic && (flags & FlagZombie) != 0)
            {
                long aggroSig = GameBridge.GetZombieAggroSignature(entity, out bool isAlerted);
                if (isAlerted)
                {
                    acc.AggroZombieCount++;
                    acc.ZombieAggroChecksum += aggroSig;
                }
            }

            if ((flags & FlagZombie) == 0)
                acc.QueuedOrderCount += MixQueue(entity, ref acc.ProductionChecksum);
        }

        private static LevelStateSnapshotDigest FinalizeDigest(DXGame game, object ls, int simTick, DigestAccumulators acc)
        {
            var digest = new LevelStateSnapshotDigest();
            ulong hash = 14695981039346656037UL;
            void Mix(long val)
            {
                hash ^= (ulong)val;
                hash *= 1099511628211UL;
            }

            digest.SimTick = simTick;
            digest.GameTimeMs = game.GameTimeInt;
            digest.Gold = GameBridge.GetLevelInt("Gold");
            digest.Wood = GameBridge.GetLevelInt("Wood");
            digest.Stone = GameBridge.GetLevelInt("Stone");
            digest.Iron = GameBridge.GetLevelInt("Iron");
            digest.Oil = GameBridge.GetLevelInt("Oil");

            Mix(HashVersion);
            Mix(digest.SimTick);
            Mix(digest.GameTimeMs);
            Mix(digest.Gold);
            Mix(digest.Wood);
            Mix(digest.Stone);
            Mix(digest.Iron);
            Mix(digest.Oil);

            long reservationChecksum = MixReservations(ls);
            digest.ReservationChecksum = reservationChecksum;
            digest.EntityCount = acc.EntityCount;
            digest.ProjectileCount = acc.ProjectileCount;
            digest.QueuedOrderCount = acc.QueuedOrderCount;
            digest.AggroZombieCount = acc.AggroZombieCount;
            digest.PosChecksum = acc.PosChecksum;
            digest.CoarsePosChecksum = acc.CoarsePosChecksum;
            digest.LifeChecksum = acc.LifeChecksum;
            digest.ZombieAggroChecksum = acc.ZombieAggroChecksum;
            digest.ProjectileChecksum = acc.ProjectileChecksum;
            digest.ProductionChecksum = acc.ProductionChecksum;
            digest.PosBuckets = acc.PosBuckets;
            digest.LifeBuckets = acc.LifeBuckets;
            digest.EntityBuckets = acc.EntityBuckets;

            Mix(acc.EntityCount);
            Mix(acc.ProjectileCount);
            Mix(acc.QueuedOrderCount);
            Mix(acc.AggroZombieCount);
            Mix(acc.LifeChecksum);
            Mix(acc.ZombieAggroChecksum);
            Mix(acc.ProjectileChecksum);
            Mix(acc.ProductionChecksum);
            Mix(reservationChecksum);
            digest.HardHash = hash;

            Mix(acc.PosChecksum);
            digest.FullHash = hash;
            return digest;
        }

        private static bool IsProjectileName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            return typeName.IndexOf("Projectile", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Proyectile", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int MixQueue(DXEntity entity, ref long productionChecksum)
        {
            IList orders;
            try { orders = GameBridge.GetOrdersQueued(GameBridge.GetCommandableObject(entity)); }
            catch (Exception ex)
            {
                if (!_reflectionWarned) { _reflectionWarned = true; ModLogger.Warn($"[StateHasher] production queue unavailable: {ex.Message}"); }
                return 0;
            }
            if (orders == null || orders.Count == 0) return 0;

            int count = 0;
            for (int slot = 0; slot < orders.Count; slot++)
            {
                object item = orders[slot];
                if (item == null) continue;
                int commandId = GameBridge.GetOrderCommandId(item);
                count++;
                // Keyed by owner entity ID + slot + command id; deterministic and order-independent.
                productionChecksum += (long)entity.ID
                    ^ ((long)(slot + 1) * 2654435761L)
                    ^ ((long)commandId * 2246822519L);
            }
            return count;
        }

        private static long MixReservations(object ls)
        {
            Type stateType = GameBridge.LevelStateType;
            if (stateType == null) return 0;

            if (_reservationProps == null || _reservationStateType != stateType)
            {
                var list = new List<PropertyInfo>();
                foreach (var p in stateType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.PropertyType != typeof(int)) continue;
                    string n = p.Name;
                    if (n.IndexOf("Reserv", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Worker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("House", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Food", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Energy", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        list.Add(p);
                    }
                }
                list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                _reservationProps = list.ToArray();
                _reservationStateType = stateType;
            }

            long sum = 0;
            for (int i = 0; i < _reservationProps.Length; i++)
            {
                try
                {
                    int v = Convert.ToInt32(_reservationProps[i].GetValue(ls));
                    sum += ((long)(i + 1) * 1000003L) ^ v;
                }
                catch { }
            }
            return sum;
        }
    }
}
