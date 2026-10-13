using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Heroes.ReplayParser;
using Heroes.ReplayParser.MPQFiles;

namespace HeroesProfile_GCP_ReplayParser
{
    // Builds the per-replay deep analytics doc (plans: deep-analytics-schema.md, section 1).
    // Times are whole seconds from game start, positions are map units, and players are
    // referred to by their index in players[]. Bump Version whenever the shape or content
    // changes; cached docs with an older version are rebuilt from the archive.
    public static class DeepExtract
    {
        public const int Version = 3;

        public static readonly string ParserVersion =
            typeof(Replay).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        private static readonly HashSet<string> FractionalDeathHeroes = new HashSet<string> { "Murky", "D.Va", "Rexxar", "The Lost Vikings" };

        public static ParseOptions ParseOptions => new ParseOptions
        {
            ShouldParseUnits = true,
            ShouldParseMouseEvents = false,
            ShouldParseDetailedBattleLobby = true,
            ShouldParseEvents = true,
            ShouldParseMessageEvents = true
        };

        public static object Failed(long replayID, string reason) => new
        {
            extract_v = Version,
            parser_version = ParserVersion,
            replay_id = replayID,
            error = reason
        };

        public static object Build(Replay replay, long replayID, string fingerprint)
        {
            var players = replay.Players;
            var index = new Dictionary<Player, int>();
            for (var i = 0; i < players.Length; i++)
                index[players[i]] = i;

            int P(Player p) => p != null && index.TryGetValue(p, out var i) ? i : -1;
            int S(TimeSpan t) => (int)t.TotalSeconds;
            int[] Pt(Point point) => point == null ? null : new[] { point.X, point.Y };

            var gameEvents = replay.GameEvents.Where(g => g.player != null && index.ContainsKey(g.player)).ToList();
            var heroUnits = new HashSet<Unit>(players.SelectMany(p => p.HeroUnits));

            return new
            {
                extract_v = Version,
                parser_version = ParserVersion,
                replay_id = replayID,
                fingerprint,
                build = replay.ReplayBuild,
                length_s = S(replay.ReplayLength),
                map_size = Pt(replay.MapSize),
                gates_open = replay.GatesOpen.HasValue ? S(replay.GatesOpen.Value) : (int?)null,

                players = players.Select((p, i) => new
                {
                    idx = i,
                    blizz_id = p.BattleNetId,
                    region = p.BattleNetRegionId,
                    team = p.Team,
                    parser_hero = string.IsNullOrEmpty(p.HeroId) ? p.Character : p.HeroId,
                    score_deaths = p.ScoreResult?.Deaths
                }),

                deaths = replay.HeroDeaths.Select(d => new
                {
                    t = S(d.TimeSpan),
                    v = P(d.Player),
                    k = d.KillingPlayers.Select(P).ToArray(),
                    x = d.Position?.X,
                    y = d.Position?.Y,
                    f = FractionalDeathHeroes.Contains(d.Player.Character)
                }),

                structures = new
                {
                    towns = replay.StructureDeaths.Select(s => new
                    {
                        t = S(s.TimeSpan),
                        town = s.TownID,
                        team = s.Team,
                        lane = s.Lane,
                        x = s.Position?.X,
                        y = s.Position?.Y,
                        k = s.KillingPlayers.Select(P).ToArray()
                    }),
                    buildings = replay.Units
                        .Where(u => u.Group == Unit.UnitGroup.Structures && u.TimeSpanDied.HasValue)
                        .Select(u => new { t = S(u.TimeSpanDied.Value), name = u.Name, team = u.Team, x = u.PointDied?.X, y = u.PointDied?.Y, killer = P(u.PlayerKilledBy) })
                },

                team_levels = replay.TeamLevels.SelectMany((levels, team) => (levels ?? new Dictionary<int, TimeSpan>())
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new { team, lvl = kv.Key, t = S(kv.Value) })),

                xp_breakdown = replay.TeamPeriodicXPBreakdown.SelectMany((breakdowns, team) => (breakdowns ?? new List<PeriodicXPBreakdown>())
                    .Select(b => new { team, t = S(b.TimeSpan), lvl = b.TeamLevel, minion = b.MinionXP, creep = b.CreepXP, structure = b.StructureXP, hero = b.HeroXP, trickle = b.TrickleXP })),

                objectives = replay.TeamObjectives.SelectMany((objectives, team) => objectives
                    .Select(o => new { team, t = S(o.TimeSpan), type = o.TeamObjectiveType.ToString(), p = P(o.Player), v = o.Value })),

                camps = replay.CampCaptures.Select(c => new { t = S(c.TimeSpan), team = c.Team, type = c.CampType, camp = c.CampID, x = c.Position?.X, y = c.Position?.Y }),

                talent_picks = players.SelectMany((p, i) => (p.Talents ?? new Talent[0])
                    .Select((talent, tier) => new { p = i, tier, id = talent.TalentID, name = talent.TalentName, t = S(talent.TimeSpanSelected) })),

                draft = replay.DraftOrder.Select((d, n) => new { n, slot = d.SelectedPlayerSlotId, hero = d.HeroSelected, type = d.PickType.ToString() }),

                disconnects = players.SelectMany((p, i) => p.Disconnects
                    .Select(d => new { p = i, left = S(d.TimeSpanLeft), rejoined = d.TimeSpanRejoined.HasValue ? S(d.TimeSpanRejoined.Value) : (int?)null })),

                regen_globes = players.Select((p, i) => new { p = i, t = p.RegenGlobePickups.Select(S).ToArray() }),

                // [t, player, x, y]: where each globe was picked up, so where the hero stood
                globe_points = GlobePoints(replay, players).Select(g => new[] { S(g.t), g.p, g.x, g.y }),

                // [t, player, x, y]: move orders (no ability) and their retargets, the last per second
                moves = gameEvents
                    .Select(g => (g, target:
                        g.eventType == GameEventType.CCmdEvent && g.data?.array?.Length > 2 && g.data.array[1] == null ? TargetPoint(g.data.array[2])
                        : g.eventType == GameEventType.CCmdUpdateTargetPointEvent ? TargetPoint(g.data)
                        : null))
                    .Where(m => m.target != null)
                    .GroupBy(m => (S(m.g.TimeSpan), index[m.g.player]))
                    .Select(m => { var last = m.Last(); return new[] { m.Key.Item1, m.Key.Item2, last.target[0], last.target[1] }; })
                    .OrderBy(m => m[0]),

                // [t, x, y, estimated]: the last point of each kind per second. Real points come from the
                // game's position events; estimated ones are move and ability target points.
                hero_positions = players.Select((p, i) => new
                {
                    p = i,
                    pts = p.HeroUnits.SelectMany(u => u.Positions)
                        .GroupBy(pos => (t: S(pos.TimeSpan), e: pos.IsEstimated))
                        .OrderBy(g => g.Key.t).ThenBy(g => g.Key.e)
                        .Select(g => { var last = g.Last(); return new[] { g.Key.t, last.Point.X, last.Point.Y, g.Key.e ? 1 : 0 }; })
                        .ToArray()
                }),

                // The parser's group list predates newer heroes (Xal'atath, Deathwing, Hogger...), so a hero
                // is any unit in a player's HeroUnits, which go by name
                units = replay.Units
                    .Where(u => heroUnits.Contains(u) || u.Group == Unit.UnitGroup.Structures || u.Group == Unit.UnitGroup.MapObjective || u.Group == Unit.UnitGroup.MercenaryCamp)
                    .Select(u => new
                    {
                        name = u.Name,
                        g = heroUnits.Contains(u) ? Unit.UnitGroup.Hero.ToString() : u.Group.ToString(),
                        team = u.Team,
                        owner = P(u.PlayerControlledBy),
                        b = new[] { S(u.TimeSpanBorn), u.PointBorn?.X ?? -1, u.PointBorn?.Y ?? -1 },
                        d = u.TimeSpanDied.HasValue ? new[] { S(u.TimeSpanDied.Value), u.PointDied?.X ?? -1, u.PointDied?.Y ?? -1 } : null,
                        killer = P(u.PlayerKilledBy)
                    }),

                unit_type_changes = replay.Units
                    .SelectMany(u => u.TypeChanges.Select(c => new { t = S(c.TimeSpan), unit = u.Name, to = c.NewName, owner = P(u.PlayerControlledBy), team = u.Team })),

                // [t, x, y, team, killer]
                minion_deaths = replay.Units
                    .Where(u => u.Group == Unit.UnitGroup.Minions && u.TimeSpanDied.HasValue)
                    .Select(u => new[] { S(u.TimeSpanDied.Value), u.PointDied?.X ?? -1, u.PointDied?.Y ?? -1, u.Team ?? -1, P(u.PlayerKilledBy) }),

                // [t, player, ability link, ability index, target x, target y]; orders with an ability only
                commands = gameEvents
                    .Where(g => g.eventType == GameEventType.CCmdEvent && g.data?.array?[1]?.array != null)
                    .Select(g =>
                    {
                        var target = TargetPoint(g.data.array[2]);
                        return new[] { S(g.TimeSpan), index[g.player], (int)(g.data.array[1].array[0].unsignedInt ?? 0), (int)(g.data.array[1].array[1].unsignedInt ?? 0), target?[0] ?? -1, target?[1] ?? -1 };
                    }),

                // Where each player's camera was centred, one point per second
                camera = players.Select((p, i) => new
                {
                    p = i,
                    pts = gameEvents
                        .Where(g => g.player == p && g.eventType == GameEventType.CCameraUpdateEvent && g.data?.array?[0]?.array != null)
                        .GroupBy(g => S(g.TimeSpan))
                        .OrderBy(g => g.Key)
                        .Select(g => { var last = g.Last().data.array[0].array; return new[] { g.Key, (int)((last[0].unsignedInt ?? 0) / 256), (int)((last[1].unsignedInt ?? 0) / 256) }; })
                        .ToArray()
                }),

                // [t, player, ability link, ability index, state]
                target_mode = gameEvents
                    .Where(g => g.eventType == GameEventType.CTriggerTargetModeUpdateEvent && g.data?.array?.Length == 3)
                    .Select(g => new[] { S(g.TimeSpan), index[g.player], (int)(g.data.array[0].unsignedInt ?? 0), (int)(g.data.array[1].unsignedInt ?? 0), (int)(g.data.array[2].vInt ?? 0) }),

                // [t, player, x, y]
                pings = gameEvents
                    .Where(g => g.eventType == GameEventType.CTriggerPingEvent && g.data?.array?.Length >= 2)
                    .Select(g => new[] { S(g.TimeSpan), index[g.player], (int)((g.data.array[0].vInt ?? 0) / 4096), (int)((g.data.array[1].vInt ?? 0) / 4096) }),

                // Alt-click announcements only; chat text is never extracted
                announcements = replay.Messages
                    .Where(m => m.MessageEventType == ReplayMessageEvents.MessageEventType.SPlayerAnnounceMessage && m.PlayerAnnounceMessage != null)
                    .Select(m => new
                    {
                        t = S(m.Timestamp),
                        p = P(m.MessageSender),
                        type = m.PlayerAnnounceMessage.AnnouncementType.ToString(),
                        link = m.PlayerAnnounceMessage.AbilityAnnouncement?.AbilityLink,
                        idx = m.PlayerAnnounceMessage.AbilityAnnouncement?.AbilityIndex
                    })
            };
        }

        // A pickup has no position, but the globe unit dies where it was taken. Several globes can go in
        // the same second (other lanes, the neutral copy on the same spot); take the one nearest the
        // player's closest real position in time, or skip the pickup when there is none to go on.
        private static IEnumerable<(TimeSpan t, int p, int x, int y)> GlobePoints(Replay replay, Player[] players)
        {
            var globes = replay.Units
                .Where(u => (u.Name == "RegenGlobe" || u.Name == "RegenGlobeNeutral") && u.TimeSpanDied.HasValue && u.PointDied != null)
                .ToList();

            for (var i = 0; i < players.Length; i++)
            {
                var player = players[i];
                var real = player.HeroUnits.SelectMany(u => u.Positions).Where(pos => !pos.IsEstimated && pos.Point != null).ToList();

                foreach (var t in player.RegenGlobePickups)
                {
                    var candidates = globes
                        .Where(u => (u.TimeSpanDied.Value - t).TotalSeconds >= -1.5 && (u.TimeSpanDied.Value - t).TotalSeconds <= 0.5)
                        .Where(u => u.Team == null || u.Team == player.Team || u.Name == "RegenGlobeNeutral")
                        .Select(u => u.PointDied)
                        .ToList();
                    if (candidates.Count == 0)
                        continue;

                    Point chosen;
                    if (candidates.All(c => c.DistanceTo(candidates[0]) <= 3))
                        chosen = candidates[0];
                    else
                    {
                        var reference = real.Where(pos => Math.Abs((pos.TimeSpan - t).TotalSeconds) <= 20)
                            .OrderBy(pos => Math.Abs((pos.TimeSpan - t).TotalSeconds)).FirstOrDefault();
                        if (reference == null)
                            continue;
                        chosen = candidates.OrderBy(c => c.DistanceTo(reference.Point)).First();
                    }

                    yield return (t, i, chosen.X, chosen.Y);
                }
            }
        }

        // CCmdEvent m_data: a target point is [x, y, z] in 1/4096 map units; a target unit carries its snapshot point at [6]
        private static int[] TargetPoint(TrackerEventStructure data)
        {
            var array = data?.array;
            if (array == null)
                return null;

            if (array.Length == 3 && array[0].unsignedInt.HasValue && array[1].unsignedInt.HasValue)
                return new[] { (int)(array[0].unsignedInt.Value / 4096), (int)(array[1].unsignedInt.Value / 4096) };

            if (array.Length > 6 && array[6]?.array != null && array[6].array.Length >= 2 && array[6].array[0].unsignedInt.HasValue && array[6].array[1].unsignedInt.HasValue)
                return new[] { (int)(array[6].array[0].unsignedInt.Value / 4096), (int)(array[6].array[1].unsignedInt.Value / 4096) };

            return null;
        }
    }
}
