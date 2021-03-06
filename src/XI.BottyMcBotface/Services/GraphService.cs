﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XI.BottyMcBotface.Models;
using XI.BottyMcBotface.Models.Settings;

namespace XI.BottyMcBotface.Services
{
    public class GraphService : IDataService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IHttpClientFactory _httpClientFactory;
        BottySettings bottySettings;

        private const string adminConsentUrlFormat = "https://login.microsoftonline.com/{0}/adminconsent?client_id={1}&redirect_uri={2}";

        private const string tenantIdClaimType = "http://schemas.microsoft.com/identity/claims/tenantid";
        private const string authorityFormat = "https://login.microsoftonline.com/{0}/v2.0";
        private const string msGraphScope = "https://graph.microsoft.com/.default";
        private const string msGraphQuery = "https://graph.microsoft.com/v1.0/users";

        public const string graphBasePathBeta = "https://graph.microsoft.com/beta";

        public GraphService(IHttpContextAccessor httpContextAccessor, IHttpClientFactory httpClientFactory, IOptions<BottySettings> settings)
        {
            _httpContextAccessor = httpContextAccessor;
            _httpClientFactory = httpClientFactory;
            bottySettings = settings.Value;
        }

        public async Task<TokenTenant> GetGraphToken(string domain)
        {
            try
            {
                var tenantId = await GetTenantId(domain);

                var daemonClient = ConfidentialClientApplicationBuilder.CreateWithApplicationOptions(new ConfidentialClientApplicationOptions() { ClientId = bottySettings.AppId, ClientSecret = bottySettings.AppSecrets, TenantId = tenantId, RedirectUri = bottySettings.Url }).Build();

                var authResult = await daemonClient.AcquireTokenForClient(new string[] { msGraphScope }).ExecuteAsync();

                var token = new TokenTenant() { Token = authResult.AccessToken, ExpireDate = authResult.ExpiresOn.DateTime };

                return token;
            }
            catch (Exception e)
            {
                return null;
            }
        }

        public async Task<string> GetTenantId(string domain)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                string tenantID = "";

                var url = "https://login.windows.net/" + domain + "/v2.0/.well-known/openid-configuration";

                var response = await client.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();
                dynamic json = JsonConvert.DeserializeObject(content);
                tenantID = json.authorization_endpoint;
                tenantID = tenantID.Substring(26, 36);

                return tenantID;
            }
        }

        public async Task<string> CreateGroup(string displayName, string description, string owner, string members, bool isPrivate)
        {
            var address = new MailAddress(owner);
            string host = address.Host;

            var token = await GetGraphToken(host);

            var client = _httpClientFactory.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/beta/groups");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            var visibility = isPrivate ? "private" : "public";

            if (string.IsNullOrEmpty(description)) description = "-";

            var membersListId = new List<string>();
            var membersList = members.Split(',').ToList();
            foreach (var item in membersList)
            {
                if (!string.IsNullOrEmpty(item)) membersListId.Add(item);
            }

            membersListId.Add(owner);

            var membersString = string.Empty;
            foreach (var item in membersListId)
            {
                membersString += $"\"https://graph.microsoft.com/beta/users/{item}\" ,";
            }

            var mailNickname = displayName.Replace(" ", "");

            var requestString = $"{{ " +
                                $" \"displayName\": \"{displayName}\", \"description\": \"{description}\" ," +
                                $" \"visibility\": \"{visibility}\" ," +
                                $" \"groupTypes\": [\"Unified\"] , \"mailEnabled\": true , \"resourceBehaviorOptions\": [\"WelcomeEmailDisabled\"] , \"securityEnabled\": false ," +
                                $" \"owners@odata.bind\": [ \"https://graph.microsoft.com/beta/users/{owner}\" ], " +
                                $" \"members@odata.bind\": [ {membersString} ], " +
                                $" \"visibility\": \"{visibility}\" ," +
                                $" \"mailNickname\": \"{mailNickname}\" }}";

            HttpContent c = new StringContent(requestString, Encoding.UTF8, "application/json");
            request.Content = c;

            var response = await client.SendAsync(request);

            var responseBody = await response.Content.ReadAsStringAsync();

            var groupId = JObject.Parse(responseBody)["id"];

            return groupId.Value<string>();
        }

        public async Task<bool> CreateTeamFromGroup(string groupId, string owner)
        {
            var address = new MailAddress(owner);
            string host = address.Host;

            var token = await GetGraphToken(host);

            var client = _httpClientFactory.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Put, $"{graphBasePathBeta}/groups/{groupId}/team");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            var payload = "{\"memberSettings\": { \"allowCreateUpdateChannels\": true },\"messagingSettings\": {\"allowUserEditMessages\": true,\"allowUserDeleteMessages\": true},\"funSettings\": {\"allowGiphy\": true,\"giphyContentRating\": \"strict\"}}";

            HttpContent c = new StringContent(payload, Encoding.UTF8, "application/json");
            request.Content = c;

            var response = await client.SendAsync(request);

            return true;
        }

        //Not used anymore
        public async Task<bool> CreateTeam(string displayName, string description, string owner, string members, bool isPrivate)
        {
            var address = new MailAddress(owner);
            string host = address.Host;

            var token = await GetGraphToken(host);

            var client = _httpClientFactory.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/beta/teams");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            var visibility = isPrivate ? "private" : "public";

            var ownerId = await RetrieveUserIdFromEmail(owner);

            var membersListId = new List<string>();
            var membersList = members.Split(',').ToList();
            foreach (var item in membersList)
            {
                string id = await RetrieveUserIdFromEmail(item);

                if (!string.IsNullOrEmpty(id)) membersListId.Add(id);
            }

            membersListId.Add(ownerId);

            var membersString = string.Empty;
            foreach (var item in membersListId)
            {
                membersString += $"\"https://graph.microsoft.com/beta/users/{item}\" ,";
            }

            var requestString = $"{{ \"template@odata.bind\": \"https://graph.microsoft.com/beta/teamsTemplates('standard')\"," +
                                $" \"displayName\": \"{displayName}\", \"description\": \"{description}\" ," +
                                $" \"visibility\": \"{visibility}\" ," +
                                $" \"owners@odata.bind\": [ \"https://graph.microsoft.com/beta/users/{ownerId}\" ] }}";

            HttpContent c = new StringContent(requestString, Encoding.UTF8, "application/json");
            request.Content = c;

            var response = await client.SendAsync(request);

            return response.IsSuccessStatusCode;
        }

        //Not used anymore
        public async Task<string> RetrieveUserIdFromEmail(string email)
        {
            try
            {
                var address = new MailAddress(email);
                string host = address.Host;

                var token = await GetGraphToken(host);

                var client = _httpClientFactory.CreateClient();

                var request = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/beta/users/{email}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

                var response = await client.SendAsync(request);

                var responseBody = await response.Content.ReadAsStringAsync();

                var userId = JObject.Parse(responseBody)["id"];

                return userId.Value<string>();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}