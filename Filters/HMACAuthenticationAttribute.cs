using Korazon.PdfGenerator.Models;
using Korazon.PdfGenerator.Properties;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Filters;
using System.Web.Http.Results;

namespace Korazon.PdfGenerator.Filters
{
    // gebaseerd op https://github.com/tjoudeh/WebApiHMACAuthentication/blob/master/HMACAuthentication.WebApi/Filters/HMACAuthenticationAttribute.cs

    public class HMACAuthenticationAttribute : Attribute, IAuthenticationFilter
    {
        private static List<RegisteredApp> allowedApps = new List<RegisteredApp>();
        private readonly long _requestMaxAgeInSeconds;
        private readonly string authenticationScheme = "amx";
        private readonly Logger _logger;

        public HMACAuthenticationAttribute()
        {
            _logger = LogManager.GetCurrentClassLogger();
            _requestMaxAgeInSeconds = Settings.Default.requestMaxAgeInSeconds;
            if (allowedApps.Count == 0)
            {
                allowedApps = JsonConvert.DeserializeObject<List<RegisteredApp>>(File.ReadAllText(Settings.Default.RegisteredAppsFile));
            }
        }

        public Task AuthenticateAsync(HttpAuthenticationContext context, CancellationToken cancellationToken)
        {
            var req = context.Request;

            try
            {
                if (req.Headers.Authorization != null && authenticationScheme.Equals(req.Headers.Authorization.Scheme, StringComparison.OrdinalIgnoreCase))
                {
                    var rawAuthzHeader = req.Headers.Authorization.Parameter;

                    var autherizationHeaderArray = GetAutherizationHeaderValues(rawAuthzHeader);

                    if (autherizationHeaderArray != null)
                    {
                        var APPId = autherizationHeaderArray[0];
                        var incomingBase64Signature = autherizationHeaderArray[1];
                        var nonce = autherizationHeaderArray[2];
                        var requestTimeStamp = autherizationHeaderArray[3];

                        var isValid = isValidRequest(req, APPId, incomingBase64Signature, nonce, requestTimeStamp);

                        if (isValid.Result)
                        {
                            var currentPrincipal = new GenericPrincipal(new GenericIdentity(APPId), null);
                            context.Principal = currentPrincipal;
                        }
                        else
                        {
                            _logger.Info($"Authorization niet valid voor url {req.RequestUri}");
                            context.ErrorResult = new UnauthorizedResult(new AuthenticationHeaderValue[0], context.Request);
                        }
                    }
                    else
                    {
                        _logger.Info($"Authorization header onjuist formaat voor url {req.RequestUri}");
                        context.ErrorResult = new UnauthorizedResult(new AuthenticationHeaderValue[0], context.Request);
                    }
                }
                else
                {
                    _logger.Info($"Authorization header niet aanwezig voor url {req.RequestUri}");
                    context.ErrorResult = new UnauthorizedResult(new AuthenticationHeaderValue[0], context.Request);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
                context.ErrorResult = new UnauthorizedResult(new AuthenticationHeaderValue[0], context.Request);
            }

            return Task.FromResult(0);
        }

        public Task ChallengeAsync(HttpAuthenticationChallengeContext context, CancellationToken cancellationToken)
        {
            context.Result = new ResultWithChallenge(context.Result);
            return Task.FromResult(0);
        }

        public bool AllowMultiple
        {
            get { return false; }
        }

        private string[] GetAutherizationHeaderValues(string rawAuthzHeader)
        {

            var credArray = rawAuthzHeader.Split(':');

            if (credArray.Length == 4)
            {
                return credArray;
            }
            else
            {
                return null;
            }

        }

        private async Task<bool> isValidRequest(HttpRequestMessage req, string APPId, string incomingBase64Signature, string nonce, string requestTimeStamp)
        {
            string requestContentBase64String = "";
            string requestUri = System.Net.WebUtility.UrlEncode(req.RequestUri.AbsoluteUri.ToLower());
            string requestHttpMethod = req.Method.Method;

            if (!allowedApps.Exists(x => x.AppId == APPId))
            {
                _logger.Info($"AppId niet gekend {APPId} voor url {req.RequestUri}");
                return false;
            }

            var sharedKey = allowedApps.First(x => x.AppId == APPId).ApiKey;

            if (isReplayRequest(nonce, requestTimeStamp))
            {
                _logger.Info($"replay request (nonce = {nonce}) voor url {req.RequestUri}");
                return false;
            }

            byte[] hash = await ComputeHash(req.Content);

            if (hash != null)
            {
                requestContentBase64String = Convert.ToBase64String(hash);
            }

            string data = String.Format("{0}{1}{2}{3}{4}{5}", APPId, requestHttpMethod, requestUri, requestTimeStamp, nonce, requestContentBase64String);

            var secretKeyBytes = Convert.FromBase64String(sharedKey);

            byte[] signature = Encoding.UTF8.GetBytes(data);

            using (HMACSHA256 hmac = new HMACSHA256(secretKeyBytes))
            {
                byte[] signatureBytes = hmac.ComputeHash(signature);
                var result = (incomingBase64Signature.Equals(Convert.ToBase64String(signatureBytes), StringComparison.Ordinal));
                if (!result)
                    _logger.Info($"incomingBase64Signature niet dezelfde voor url {req.RequestUri}");

                return result;
            }

        }

        private bool isReplayRequest(string nonce, string requestTimeStamp)
        {
            if (System.Runtime.Caching.MemoryCache.Default.Contains(nonce))
            {
                return true;
            }

            DateTime epochStart = new DateTime(1970, 01, 01, 0, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan currentTs = DateTime.UtcNow - epochStart;

            var serverTotalSeconds = Convert.ToInt64(currentTs.TotalSeconds);
            var requestTotalSeconds = Convert.ToInt64(requestTimeStamp);

            if (Math.Abs(serverTotalSeconds - requestTotalSeconds) > _requestMaxAgeInSeconds)
            {
                _logger.Info($"replayrequest detected: requestTimeStamp = {requestTimeStamp}, serverTotalSeconds = {serverTotalSeconds}, servertime = {DateTime.UtcNow}");
                return true;
            }

            System.Runtime.Caching.MemoryCache.Default.Add(nonce, requestTimeStamp, DateTimeOffset.UtcNow.AddSeconds(_requestMaxAgeInSeconds));

            return false;
        }

        private static async Task<byte[]> ComputeHash(HttpContent httpContent)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = null;
                var content = await httpContent.ReadAsByteArrayAsync();
                if (content.Length != 0)
                {
                    hash = md5.ComputeHash(content);
                }
                return hash;
            }
        }
    }

    public class ResultWithChallenge : IHttpActionResult
    {
        private readonly string authenticationScheme = "amx";
        private readonly IHttpActionResult next;

        public ResultWithChallenge(IHttpActionResult next)
        {
            this.next = next;
        }

        public async Task<HttpResponseMessage> ExecuteAsync(CancellationToken cancellationToken)
        {
            var response = await next.ExecuteAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(authenticationScheme));
            }

            return response;
        }
    }
}