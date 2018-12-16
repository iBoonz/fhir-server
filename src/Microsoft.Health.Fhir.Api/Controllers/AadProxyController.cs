// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Api.Features.ActionResults;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Linq;

namespace Microsoft.Health.Fhir.Api.Controllers
{
    /// <summary>
    /// Controller class enabling Azure Active Directory SMART on FHIR Proxy Capability
    /// </summary>
    [TypeFilter(typeof(AadProxyFeatureFilterAttribute))]
    [Route("/AadProxy")]
    public class AadProxyController : Controller
    {
        private readonly SecurityConfiguration _securityConfiguration;
        private readonly bool _isAadV2;
        private readonly ILogger<SecurityConfiguration> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _aadAuthorizeEndpoint;
        private readonly string _aadTokenEndpoint;

        // TODO: _launchContextFields contain a list of fields that we will transmit as part of launch context, should be configurable
        private readonly string[] _launchContextFields = { "patient", "encounter", "practitioner", "need_patient_banner", "smart_style_url" };

        /// <summary>
        /// Initializes a new instance of the <see cref="AadProxyController" /> class.
        /// </summary>
        /// <param name="securityConfiguration">Security configuration parameters.</param>
        /// <param name="httpClientFactory">HTTP Client Factory.</param>
        /// <param name="logger">The logger.</param>
        public AadProxyController(IOptions<SecurityConfiguration> securityConfiguration, IHttpClientFactory httpClientFactory, ILogger<SecurityConfiguration> logger)
        {
            EnsureArg.IsNotNull(securityConfiguration, nameof(securityConfiguration));

            _securityConfiguration = securityConfiguration.Value;
            _isAadV2 = new Uri(_securityConfiguration.Authentication.Authority).Segments.Contains("v2.0");
            _logger = logger;
            _httpClientFactory = httpClientFactory;

            var openIdConfigurationUrl = $"{_securityConfiguration.Authentication.Authority}/.well-known/openid-configuration";

            HttpResponseMessage openIdConfigurationResponse;
            using (var httpClient = httpClientFactory.CreateClient())
            {
                try
                {
                    openIdConfigurationResponse = httpClient.GetAsync(new Uri(openIdConfigurationUrl)).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, $"There was an exception while attempting to read the OpenId Configuration from \"{openIdConfigurationUrl}\".");
                    throw new OpenIdConfigurationException();
                }
            }

            if (openIdConfigurationResponse.IsSuccessStatusCode)
            {
                var openIdConfiguration = JObject.Parse(openIdConfigurationResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult());

                try
                {
                    _aadTokenEndpoint = openIdConfiguration["token_endpoint"].Value<string>();
                    _aadAuthorizeEndpoint = openIdConfiguration["authorization_endpoint"].Value<string>();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, $"There was an exception while attempting to read the endpoints from \"{openIdConfigurationUrl}\".");
                    throw new OpenIdConfigurationException();
                }
            }
            else
            {
                throw new OpenIdConfigurationException();
            }
        }

        /// <summary>
        /// Proxies a request to the Azure AD authorize endpoint.
        /// </summary>
        /// <param name="responseType">response_type URL parameter.</param>
        /// <param name="clientId">client_id URL parameter.</param>
        /// <param name="redirectUri">redirect_uri URL parameter.</param>
        /// <param name="launch">launch (launch context)URL parameter.</param>
        /// <param name="scope">scope URL parameter.</param>
        /// <param name="state">state URL parameter.</param>
        /// <param name="aud">aud (audience) URL parameter.</param>
        [HttpGet("authorize")]
        public ActionResult Authorize(
            [FromQuery(Name = "response_type")] string responseType,
            [FromQuery(Name = "client_id")] string clientId,
            [FromQuery(Name = "redirect_uri")] Uri redirectUri,
            [FromQuery(Name = "launch")] string launch,
            [FromQuery(Name = "scope")] string scope,
            [FromQuery(Name = "state")] string state,
            [FromQuery(Name = "aud")] string aud)
        {
            EnsureArg.IsNotNull(responseType, nameof(responseType));
            EnsureArg.IsNotNull(clientId, nameof(clientId));
            EnsureArg.IsNotNull(redirectUri, nameof(redirectUri));
            EnsureArg.IsNotNull(aud, nameof(aud));

            if (string.IsNullOrEmpty(launch))
            {
                launch = Base64UrlEncoder.Encode("{}");
            }

            JObject newStateObj = JObject.Parse("{}");
            newStateObj.Add("s", state);
            newStateObj.Add("l", launch);

            string newState = Base64UrlEncoder.Encode(newStateObj.ToString(Newtonsoft.Json.Formatting.None));

            Uri callbackUrl = new Uri(
                Request.Scheme + "://" + Request.Host + "/AadProxy/callback/" +
                Base64UrlEncoder.Encode(redirectUri.ToString()));

            StringBuilder queryStringBuilder = new StringBuilder();
            queryStringBuilder.Append($"response_type={responseType}&redirect_uri={callbackUrl.ToString()}&client_id={clientId}");
            if (!_isAadV2)
            {
                queryStringBuilder.Append($"&resource={aud}");
            }
            else
            {
                // Azure AD v2.0 uses fully qualified scopes and does not allow '/' (slash)
                // We add qualification to scopes and replace '/' -> '$'

                EnsureArg.IsNotNull(scope, nameof(scope));
                var scopes = scope.Split(' ');
                StringBuilder scopesBuilder = new StringBuilder();
                string[] wellKnownScopes = { "profile", "openid", "email", "offline_access" };

                foreach (var s in scopes)
                {
                    if (wellKnownScopes.Contains(s))
                    {
                        scopesBuilder.Append($"{s} ");
                    }
                    else
                    {
                        scopesBuilder.Append($"{aud}/{s.Replace('/', '$')} ");
                    }
                }

                var newScopes = scopesBuilder.ToString().TrimEnd(' ');
                queryStringBuilder.Append($"&scope={Uri.EscapeDataString(newScopes)}");
            }

            queryStringBuilder.Append($"&state={newState}");

            return Redirect($"{_aadAuthorizeEndpoint}?{queryStringBuilder.ToString()}");
        }

        /// <summary>
        /// Callback function for receiving code from AAD
        /// </summary>
        /// <param name="encodedRedirect">Base64url encoded redirect URL on the app.</param>
        /// <param name="code">Authorization code.</param>
        /// <param name="state">state URL parameter.</param>
        /// <param name="sessionState">session_state URL parameter.</param>
        /// <param name="error">error URL parameter.</param>
        /// <param name="errorDescription">error_description URL parameter.</param>
        [HttpGet("callback/{encodedRedirect}")]
        public ActionResult Callback(
            string encodedRedirect,
            [FromQuery(Name = "code")] string code,
            [FromQuery(Name = "state")] string state,
            [FromQuery(Name = "session_state")] string sessionState,
            [FromQuery(Name = "error")] string error,
            [FromQuery(Name = "error_description")] string errorDescription)
        {
            Uri redirectUrl = new Uri(Base64UrlEncoder.Decode(encodedRedirect));

            if (!string.IsNullOrEmpty(error))
            {
                return Redirect($"{redirectUrl.ToString()}?error={error}&error_description={errorDescription}");
            }

            string compoundCode;
            string newState;
            try
            {
                JObject launchStateParameters = JObject.Parse(Base64UrlEncoder.Decode(state));
                JObject launchParameters = JObject.Parse(Base64UrlEncoder.Decode(launchStateParameters["l"].ToString()));
                launchParameters.Add("code", code);
                newState = launchStateParameters["s"].ToString();
                compoundCode = Base64UrlEncoder.Encode(launchParameters.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch
            {
                _logger.LogError("Error parsing launch parameters.");
                throw;
            }

            return RedirectPermanent($"{redirectUrl.ToString()}?code={compoundCode}&state={newState}&session_state={sessionState}");
        }

        /// <summary>
        /// Proxies a (POST) request to the AAD token endpoint
        /// </summary>
        /// <param name="grantType">grant_type request parameter.</param>
        /// <param name="compoundCode">The base64url encoded code and launch context</param>
        /// <param name="redirectUri">redirect_uri request parameter.</param>
        /// <param name="clientId">client_id request parameter.</param>
        /// <param name="clientSecret">client_secret request parameter.</param>
        [HttpPost("token")]
        public async Task<ActionResult> Token(
            [FromForm(Name = "grant_type")] string grantType,
            [FromForm(Name = "code")] string compoundCode,
            [FromForm(Name = "redirect_uri")] Uri redirectUri,
            [FromForm(Name = "client_id")] string clientId,
            [FromForm(Name = "client_secret")] string clientSecret)
        {
            EnsureArg.IsNotNull(grantType, nameof(grantType));
            EnsureArg.IsNotNull(clientId, nameof(clientId));

            var client = _httpClientFactory.CreateClient();

            // Azure AD supports client_credentials, etc.
            // These are used in tests and may have value even when SMART proxy is used.
            // This prevents disabling those options.
            // TODO: Add handling of 'aud' -> 'resource', should that be an error or should translation be done?
            if (grantType != "authorization_code")
            {
                List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>>();
                foreach (var f in Request.Form)
                {
                    fields.Add(new KeyValuePair<string, string>(f.Key, f.Value));
                }

                var passThroughContent = new FormUrlEncodedContent(fields);

                var passThroughResponse = await client.PostAsync(new Uri(_aadTokenEndpoint), passThroughContent);

                return new ContentResult()
                {
                    Content = await passThroughResponse.Content.ReadAsStringAsync(),
                    StatusCode = (int)passThroughResponse.StatusCode,
                    ContentType = "application/json",
                };
            }

            EnsureArg.IsNotNull(compoundCode, nameof(compoundCode));
            EnsureArg.IsNotNull(redirectUri, nameof(redirectUri));

            JObject decodedCompoundCode;
            string code;
            try
            {
                decodedCompoundCode = JObject.Parse(Base64UrlEncoder.Decode(compoundCode));
                code = decodedCompoundCode["code"].ToString();
            }
            catch
            {
                _logger.LogError("Error decoding compound code");
                throw;
            }

            Uri callbackUrl = new Uri(
                Request.Scheme + "://" + Request.Host + "/AadProxy/callback/" +
                Base64UrlEncoder.Encode(redirectUri.ToString()));

            // TODO: Deal with client secret in basic auth header
            var content = new FormUrlEncodedContent(
                new[]
                {
                    new KeyValuePair<string, string>("grant_type", grantType),
                    new KeyValuePair<string, string>("code", code),
                    new KeyValuePair<string, string>("redirect_uri", callbackUrl.ToString()),
                    new KeyValuePair<string, string>("client_id", clientId),
                    new KeyValuePair<string, string>("client_secret", clientSecret),
                });

            var response = await client.PostAsync(new Uri(_aadTokenEndpoint), content);

            if (!response.IsSuccessStatusCode)
            {
                return new ContentResult()
                {
                    Content = await response.Content.ReadAsStringAsync(),
                    StatusCode = (int)response.StatusCode,
                    ContentType = "application/json",
                };
            }

            var tokenResponse = JObject.Parse(await response.Content.ReadAsStringAsync());

            // Handle fields passed through launch context
            foreach (var launchField in _launchContextFields)
            {
                if (decodedCompoundCode[launchField] != null)
                {
                    tokenResponse[launchField] = decodedCompoundCode[launchField];
                }
            }

            tokenResponse["client_id"] = clientId;

            // Replace fully qualifies scopes with short scopes and replace $
            string[] scopes = tokenResponse["scope"].ToString().Split(' ');
            StringBuilder scopesBuilder = new StringBuilder();

            foreach (var s in scopes)
            {
                if (IsAbsoluteUrl(s))
                {
                    Uri scopeUri = new Uri(s);
                    scopesBuilder.Append($"{scopeUri.Segments.Last().Replace('$', '/')} ");
                }
                else
                {
                    scopesBuilder.Append($"{s.Replace('$', '/')} ");
                }
            }

            tokenResponse["scope"] = scopesBuilder.ToString().TrimEnd(' ');

            return new ContentResult()
            {
                Content = tokenResponse.ToString(Newtonsoft.Json.Formatting.None),
                StatusCode = (int)response.StatusCode,
                ContentType = "application/json",
            };
        }

        private static bool IsAbsoluteUrl(string url)
        {
            Uri result;
            return Uri.TryCreate(url, UriKind.Absolute, out result);
        }
    }
}