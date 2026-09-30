using Google.Cloud.Functions.Framework;
using Google.Cloud.Functions.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System;

using Heroes.ReplayParser;
using Google.Cloud.Storage.V1;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using Google;

namespace HeroesProfile_GCP_ReplayParser
{
    [FunctionsStartup(typeof(Startup))]
    public class Function : IHttpFunction
    {
        private readonly ILogger<Function> _logger;
        private readonly StorageClient _storageClient;

        public Function(ILogger<Function> logger, StorageClient storageClient)
        {
            _logger = logger;
            _storageClient = storageClient;
        }

        // A replay is under 10MB; a download still going after this is a stalled connection.
        private static readonly TimeSpan DownloadAttemptTimeout = TimeSpan.FromSeconds(15);
        private const int DownloadAttempts = 3;

        public async Task HandleAsync(HttpContext context)
        {
            InputData data = null;

            try
            {
                using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
                {
                    var requestBody = await reader.ReadToEndAsync();
                    data = InputData.FromJson(requestBody);
                }

                await ProcessAsync(context, data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Request failed: bucket={Bucket}, input={Input}, parseType={ParseType}",
                    data?.Bucket, data?.Input, data?.ParseType);

                // Without a body the caller only ever sees an empty 500.
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsync($"Parser error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task ProcessAsync(HttpContext context, InputData data)
        {
            var totalStopwatch = Stopwatch.StartNew();

            var downloadStopwatch = Stopwatch.StartNew();
            byte[] bytes = await DownloadAsync(data.Bucket, data.Input);
            downloadStopwatch.Stop();

            var parseOptions = GetParseOptions(data.ParseType);

            var parseStopwatch = Stopwatch.StartNew();
            var result = DataParser.ParseReplay(bytes, parseOptions);
            parseStopwatch.Stop();

            if (result.Item1 != DataParser.ReplayParseResult.Success || result.Item2 == null)
            {
                _logger.LogWarning("Replay parse failed: result={ParseResult}, input={Input}",
                    result.Item1, data.Input);

                await context.Response.WriteAsync($"Error parsing replay: {result.Item1}");
            }
            else
            {
                string calculatedFingerprint = GetFingerprint(result.Item2);

                if (data.ParseType != "fingerprintOnly")
                {
                    bool match = calculatedFingerprint == data.Fingerprint;

                    var cameraDistance = new Dictionary<Player, int>();
                    if (data.ParseType == "default")
                    {
                        cameraDistance = DetermineCameraDistancePerPlayer(result.Item2);
                    }

                    int uploadTeam;
                    try
                    {
                        uploadTeam = CalculateUploadTeam(result.Item2);
                    }
                    catch
                    {
                        uploadTeam = -1;
                    }

                    var returnData = ToJson(result.Item2, match, calculatedFingerprint, uploadTeam, cameraDistance);
                    await context.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(returnData));
                }
                else
                {
                    var obj = new
                    {
                        fingerprint = calculatedFingerprint,
                        game_date = result.Item2.Timestamp
                    };

                    await context.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(obj));
                }
            }

            totalStopwatch.Stop();
            _logger.LogInformation("Parsed replay: input={Input}, parseType={ParseType}, result={ParseResult}, size={SizeBytes} bytes, download={DownloadMs}ms, parse={ParseMs}ms, total={TotalMs}ms",
                data.Input, data.ParseType, result.Item1, bytes.Length,
                downloadStopwatch.ElapsedMilliseconds, parseStopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds);
        }

        private async Task<byte[]> DownloadAsync(string bucket, string name)
        {
            for (int attempt = 1; ; attempt++)
            {
                using var timeout = new CancellationTokenSource(DownloadAttemptTimeout);
                using var stream = new MemoryStream();

                try
                {
                    await _storageClient.DownloadObjectAsync(bucket, name, stream, cancellationToken: timeout.Token);
                    return stream.ToArray();
                }
                catch (Exception ex) when (attempt < DownloadAttempts && IsTransient(ex))
                {
                    _logger.LogWarning(ex, "Download attempt {Attempt} failed for {Input}, retrying", attempt, name);
                }
            }
        }

        // Stalls and reset connections recover on a fresh attempt; a missing object or bad request never will.
        private static bool IsTransient(Exception ex) => ex switch
        {
            GoogleApiException api => api.HttpStatusCode == HttpStatusCode.TooManyRequests || (int)api.HttpStatusCode >= 500,
            OperationCanceledException => true,
            HttpRequestException => true,
            IOException => true,
            _ => false
        };

        private static ParseOptions GetParseOptions(string parseType) => parseType switch
        {
            "fingerprintOnly" => new ParseOptions
            {
                ShouldParseUnits = false,
                ShouldParseMouseEvents = false,
                ShouldParseDetailedBattleLobby = true,
                ShouldParseEvents = false,
                ShouldParseMessageEvents = false
            },
            "fallback1" => ParseOptions.MediumParsing,
            "fallback2" => ParseOptions.DefaultParsing,
            "fallback3" => ParseOptions.MinimalParsing,
            "full" => ParseOptions.FullParsing,
            _ => new ParseOptions
            {
                ShouldParseUnits = false,
                ShouldParseMouseEvents = false,
                ShouldParseDetailedBattleLobby = true,
                ShouldParseEvents = true,
                ShouldParseMessageEvents = true
            }
        };

        public static object ToJson(Replay replay, bool match, string calculatedFingerprint, int uploadTeam, Dictionary<Player, int> cameraDistance)
        {
            var obj = new
            {
                random_value = replay.RandomValue,
                calculated_fingerprint = calculatedFingerprint,
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
                upload_team = uploadTeam,
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

        private static int CalculateUploadTeam(Replay replay)
        {
            if (replay.Messages.Count > 0)
            {
                int playerIndex = replay.Messages[0].PlayerIndex;
                return replay.Players[playerIndex].Team;
            }
            return -1;
        }

        private static Dictionary<Player, int> DetermineCameraDistancePerPlayer(Replay replay)
        {
            var cameraDistance = new Dictionary<Player, int>();

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
