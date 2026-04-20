using Microsoft.Extensions.Logging;
using OVS.Rollback.Configuration;
using OVS.Rollback.Core;
using OVS.Rollback.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using OVS.Rollback.Utils;
using static OVS.Rollback.Core.Constants;
using static OVS.Rollback.Core.LoggerTemplates;
using OVS.Rollback.Common;

namespace OVS.Rollback.Utils
{
    internal class HTTPHelper
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger = Utilities.NewLogger<HTTPHelper>();
        private string? _baseURL;
        private static readonly string LogPrefix = Utilities.GetLogPrefix<HTTPHelper>();

        protected internal ILogger Logger { get => _logger; }
        public string BaseUrl
        {
            get {
                _baseURL ??= Utilities.GetBaseUrlFromEnv(Logger);
                return _baseURL;
            }
        }
        public bool IsOVS { get; private set; }
        public bool IsMVSI { get; private set; }

        public string RegisterPath { get => IsOVS ? Endpoints.OVSRegister : Endpoints.MVSIRegister; }

        public string RegisterURL { get => BaseUrl + RegisterPath; }

        public string EndMatchPath { get => IsOVS ? Endpoints.OVSEndMatch : Endpoints.MVSIEndMatch; }

        public string EndMatchURL { get => BaseUrl + EndMatchPath; }

        public string MatchStatusPath { get => Endpoints.OVSMatchStatus; }
        public string MatchStatusURL { get => BaseUrl + MatchStatusPath; }

        private ServerConfiguration Config { get => ServerConfiguration.Instance; }

        private string parsedMatchUpdateKey { get => Config.Server.MatchUpdateKey ?? "MisconfiguredMatchUpdateKey"; }

        public bool BeVerbose { get => Config.Server.VerboseLogging; }

        public HTTPHelper()
        {
            _httpClient = new HttpClient {
                Timeout = TimeSpan.FromSeconds(Config.Networking.HttpTimeoutSeconds)
            };
            _logger = Utilities.NewLogger<HTTPHelper>();
            Init();
        }

        public HTTPHelper(ILogger logger)
        {
            _httpClient = new HttpClient {
                Timeout = TimeSpan.FromSeconds(Config.Networking.HttpTimeoutSeconds)
            };
            _logger = logger;
            Init();
        }

        public HTTPHelper(HttpClient httpClient)
        {
            _httpClient ??= httpClient;
            _logger = Utilities.NewLogger<HTTPHelper>();
            Init();
        }

        public HTTPHelper(ILogger logger, HttpClient httpClient)
        {
            _httpClient ??= httpClient;
            _logger = (ILogger<HTTPHelper>)logger;
            Init();
        }

        private void Init()
        {
            _baseURL = Utilities.GetBaseUrlFromEnv(Logger);
            IsOVS = Utilities.IsOVS;
            IsMVSI = Utilities.IsMVSI;
        }

        public async Task<HttpResponseMessage> PostJsonAsync(string url, object data, Dictionary<string, string>? headers = null)
        {
            HttpResponseMessage response;
            Dictionary<string, string> requestHeaders = headers ?? new Dictionary<string, string>();

            if (!requestHeaders.ContainsKey("MatchUpdateKey"))
            {
                requestHeaders.Add("MatchUpdateKey", parsedMatchUpdateKey);
            }
            else if (requestHeaders["MatchUpdateKey"].StringIsNullOrWhiteSpace)
            {
                requestHeaders["MatchUpdateKey"] = parsedMatchUpdateKey;
            }

            try
            {
                string json = JsonSerializer.Serialize(data);
                string jsonAsB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                var contentHash = Utilities.CreateHMAC<string>(parsedMatchUpdateKey, jsonAsB64, HMACType.Hexlower);
                requestHeaders.Add("BodyAsBase64", jsonAsB64);
                //requestHeaders["MatchUpdateKey"] = contentHash;
                requestHeaders["MatchUpdateKey"] = parsedMatchUpdateKey.ToString();

                var content = new StringContent(json, Encoding.UTF8, "application/json");

                foreach (var header in requestHeaders)
                {
                    content.Headers.Add(header.Key, header.Value);
                }

                if (BeVerbose)
                {
                    _logger.LogInformation("{LogPrefix} Sending POST request to {Url} with HMAC hash: {hash} and payload: {Payload}", LogPrefix, url, contentHash, json);
                }
                response = await _httpClient.PostAsync(url, content);

                if (null != response && BeVerbose)
                {
                    _logger.LogInformation("{LogPrefix} Received response with status code: {StatusCode}", LogPrefix, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{LogPrefix} Error sending POST request to {Url}: {Message}", LogPrefix, url, ex.Message);
                throw;
            }

            return response ?? throw new Exception($"Failed to receive a response from POST request to {url}");
        }

        public async Task<string> PostJsonAsync(string url, object data, Dictionary<string, string>? headers = null, bool? returnBody = false)
        {
            Dictionary<string, string> requestHeaders = headers ?? new Dictionary<string, string>();

            if (!requestHeaders.ContainsKey("MatchUpdateKey"))
            {
                requestHeaders.Add("MatchUpdateKey", parsedMatchUpdateKey);
            }
            else if (requestHeaders["MatchUpdateKey"].StringIsNullOrWhiteSpace)
            {
                requestHeaders["MatchUpdateKey"] = parsedMatchUpdateKey;
            }

            try
            {
                var response = await PostJsonAsync(url, data, requestHeaders);
                var body = await response.Content.ReadAsStringAsync() ?? string.Empty;
                return body;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{LogPrefix} Error sending POST request to {Url}: {Message}", LogPrefix, url, ex.Message);
                throw;
            }
        }

        protected internal async Task<OVSMatchConfig?> FetchMatchConfigAsync(string matchId, string key)
        {
            try
            {
                var body = await PostJsonAsync(
                    url: RegisterURL,
                    data: new RegisterPayload {
                        MatchId = matchId,
                        Key = key
                    },
                    returnBody: true);
                return JsonSerializer.Deserialize<OVSMatchConfig>(body);
            }
            catch (Exception ex)
            {
                Log.FetchConfigFailed(_logger, matchId, ex);
                return null;
            }
        }

        protected internal async Task SendEndMatchAsync(string matchId, string key)
        {
            try
            {
                await PostJsonAsync(
                    url: EndMatchURL,
                    data: new RegisterPayload {
                        MatchId = matchId,
                        Key = key
                    },
                    returnBody: false);

                Log.MatchEnded(_logger, matchId, EndMatchURL);
            }
            catch (Exception ex)
            {
                Log.EndMatchFailed(_logger, EndMatchURL, ex);
            }
        }

        protected internal async Task<MatchStatusResponse?> SendMatchStatus(StatusEventArgs matchStatus)
        {
            var payload = matchStatus.StatusObject;

            try
            {
                var body = await PostJsonAsync(
                    url: MatchStatusURL,
                    data: payload,
                    returnBody: true);

                return JsonSerializer.Deserialize<MatchStatusResponse>(body);
            }
            catch (Exception ex)
            {
                Log.SendMatchStatusFailed(_logger, matchStatus.StatusObject.Event, matchStatus.StatusObject.MatchId, ex);
                return null;
            }
        }
    }
}
