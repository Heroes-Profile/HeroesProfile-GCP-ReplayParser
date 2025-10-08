using Google.Cloud.Functions.Framework;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System;

using Heroes.ReplayParser;
using Google.Cloud.Storage.V1;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HeroesProfile_GCP_ReplayParser
{
    public class Function : IHttpFunction
    {
 
        /// <summary>
        /// Logic for your function goes here.
        /// </summary>
        /// <param name="context">The HTTP context, containing the request and the response.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task HandleAsync(HttpContext context)
        {
            InputData data = new InputData();

            using (StreamReader reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            {
                string requestBody = await reader.ReadToEndAsync();
                data = InputData.FromJson(requestBody);
            }

            var client = StorageClient.Create();


            byte[] bytes;

            using (var stream = new MemoryStream())
            {
                await client.DownloadObjectAsync(data.Bucket, data.Input, stream);

                using (var dst = new MemoryStream())
                {
                    bytes = stream.GetBuffer();
                }
            }

            ParseOptions parseOptions = new ParseOptions
            {
                ShouldParseUnits = false,
                ShouldParseMouseEvents = false,
                ShouldParseDetailedBattleLobby = true,
                ShouldParseEvents = true,
                ShouldParseMessageEvents = true
            };


            switch (data.ParseType)
            {
                case "fingerprintOnly":
                    parseOptions = new ParseOptions
                    {
                        ShouldParseUnits = false,
                        ShouldParseMouseEvents = false,
                        ShouldParseDetailedBattleLobby = true,
                        ShouldParseEvents = false,
                        ShouldParseMessageEvents = false
                    };
                    break;

                case "fallback1":
                    parseOptions = ParseOptions.MediumParsing;
                    break;

                case "fallback2":
                    parseOptions = ParseOptions.DefaultParsing;
                    break;

                case "fallback3":
                    parseOptions = ParseOptions.MinimalParsing;
                    break;

                case "full":
                    parseOptions = ParseOptions.FullParsing;
                    break;

                default:
                    parseOptions = new ParseOptions
                    {
                        ShouldParseUnits = false,
                        ShouldParseMouseEvents = false,
                        ShouldParseDetailedBattleLobby = true,
                        ShouldParseEvents = true,
                        ShouldParseMessageEvents = true
                    };
                    break;
            }
    

            var result = DataParser.ParseReplay(bytes, parseOptions);



            if (result.Item1 != DataParser.ReplayParseResult.Success || result.Item2 == null)
            {
                await context.Response.WriteAsync($"Error parsing replay: {result.Item1}");
            }
            else
            {
                string calculated_fingerprint = GetFingerprint(result.Item2);

                if (data.ParseType != "fingerprintOnly")
                {
                    bool match = true;

                    if (calculated_fingerprint != data.Fingerprint)
                    {
                        match = false;
                    }

                    Dictionary<Player, int> cameraDistance = new Dictionary<Player, int>();
                    if (data.ParseType == "default")
                    {
                        cameraDistance = determineCameraDistancePerPlayer(result.Item2);
                    }
                    int upload_team;

                    try
                    {
                        upload_team = calculateUploadTeam(result.Item2);
                    }
                    catch
                    {
                        upload_team = -1;
                    }
                    var return_data = ToJson(result.Item2, match, calculated_fingerprint, upload_team, cameraDistance);

                    await context.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(return_data));
                }
                else
                {
                    var obj = new
                    {
                        fingerprint = calculated_fingerprint,
                        game_date = result.Item2.Timestamp
                    };

                    await context.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(obj));
                }
            }
        }

        public static object ToJson(Replay replay, bool match, string calculated_fingerprint, int upload_team, Dictionary<Player, int> cameraDistance)
        {
            var obj = new
            {
                random_value = replay.RandomValue,
                calculated_fingerprint = calculated_fingerprint,
                fingerprint_match = match,
                mode = replay.GameMode.ToString(),
                region = replay.Players[0].BattleNetRegionId,
                date = replay.Timestamp,
                length = replay.ReplayLength,
                map = replay.Map,
                map_short = replay.MapAlternativeName,
                version = replay.ReplayVersion,
                version_major = replay.ReplayVersionMajor,
                version_build = replay.ReplayBuild,
                bans = replay.TeamHeroBans,
                draft_order = replay.DraftOrder,
                team_experience = replay.TeamPeriodicXPBreakdown,
                upload_team = upload_team,
                players = from p in replay.Players
                          select new
                          {
                              battletag_name = p.Name,
                              battletag_id = p.BattleTag,
                              blizz_id = p.BattleNetId,
                              camera_distance = cameraDistance.ContainsKey(p) ? cameraDistance[p] : 0,
                              account_level = p.AccountLevel,
                              hero = p.Character,
                              hero_level = p.CharacterLevel,
                              hero_level_taunt = p.HeroMasteryTiers,
                              team = p.Team,
                              winner = p.IsWinner,
                              silenced = p.IsSilenced,
                              party = p.PartyValue,
                              talents = p.Talents.Select(t => t.TalentName),
                              score = p.ScoreResult,
                              staff = p.IsBlizzardStaff,
                              announcer = p.AnnouncerPackAttributeId,
                              banner = p.BannerAttributeId,
                              skin_title = p.SkinAndSkinTint,
                              hero_skin = p.SkinAndSkinTintAttributeId,
                              mount_title = p.MountAndMountTint,
                              mount = p.MountAndMountTintAttributeId,
                              spray_title = p.Spray,
                              spray = p.SprayAttributeId,
                              voice_line_title = p.VoiceLine,
                              voice_line = p.VoiceLineAttributeId,
                          }
            };
            return obj;
        }

        private static string GetFingerprint(Replay replay)
        {
            var str = new StringBuilder();
            replay.Players.Select(p => p.BattleNetId).OrderBy(x => x).Map(x => str.Append(x.ToString()));
            str.Append(replay.RandomValue);
            var md5 = MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(str.ToString()));
            var result = new Guid(md5);
            return result.ToString();
        }


        private static int calculateUploadTeam(Replay replay)
        {
            int team = -1;

            if (replay.Messages.Count > 0)
            {
                int playerIndex = replay.Messages[0].PlayerIndex;

                var player = replay.Players[playerIndex];

                team = player.Team;
            }
            return team;
        }

        private static Dictionary<Player, int> determineCameraDistancePerPlayer(Replay replay)
        {
            Dictionary<Player, int> cameraDistance = new Dictionary<Player, int>();

            for (int i = 0; i < replay.GameEvents.Count; i++)
            {
                if (replay.GameEvents[i].eventType.ToString() == "CCameraUpdateEvent")
                {
                    try
                    {

                        if (!cameraDistance.ContainsKey(replay.GameEvents[i].player))
                        {
                            cameraDistance.Add(replay.GameEvents[i].player, Convert.ToInt32(replay.GameEvents[i].data.array[1].unsignedInt));
                        }

                    }
                    catch
                    {

                    }

                }
            }
            return cameraDistance;
        }
    }
}
