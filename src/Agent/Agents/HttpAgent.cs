﻿using Agent.Cbor;
using Dahomey.Cbor;
using ICP.Agent.Auth;
using ICP.Agent.Requests;
using ICP.Agent.Responses;
using ICP.Candid.Models;
using ICP.Candid.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace ICP.Agent.Agents
{
    public class HttpAgent : IAgent
    {
        private const string CBOR_CONTENT_TYPE = "application/cbor";

        private static readonly Lazy<CborOptions> cborOptionsLazy = new Lazy<CborOptions>(() =>
        {
            var options = new CborOptions();
            var provider = new CborConverterProvider();
            options.Registry.ConverterRegistry.RegisterConverterProvider(provider);
            options.Registry.ConverterRegistry.RegisterConverter(typeof(IHashable), new HashableCborConverter(options.Registry.ConverterRegistry));
            return options;
        }, isThreadSafe: true);

        public IIdentity Identity { get; }

        private readonly HttpClient httpClient;

        public HttpAgent(IIdentity identity, Uri? boundryCanisterUrl = null)
        {
            this.Identity = identity;
            this.httpClient = new HttpClient
            {
                BaseAddress = boundryCanisterUrl
            };
            this.httpClient.DefaultRequestHeaders
                .Accept
                .Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(CBOR_CONTENT_TYPE));
        }

        public async Task CallAsync(Principal canisterId, string method, CandidArg encodedArgument, Principal? targetCanisterOverride = null, IIdentity? identityOverride = null)
        {
            if (targetCanisterOverride == null)
            {
                targetCanisterOverride = canisterId;
            }
            await this.SendWithNoResponseAsync($"/api/v2/canister/{targetCanisterOverride.ToText()}/query", BuildRequest, identityOverride);

            CallRequest BuildRequest(Principal sender, ICTimestamp now)
            {
                return new CallRequest(canisterId, method, encodedArgument, sender, now);
            }
        }

        public Principal GetPrincipal()
        {
            return this.Identity.Principal;
        }

        public async Task<Key> GetRootKeyAsync()
        {
            // TODO cache
            StatusResponse jsonObject = await this.GetStatusAsync();
            return jsonObject.DevelopmentRootKey ?? throw new NotImplementedException("Get root key from trusted source");
        }

        public async Task<StatusResponse> GetStatusAsync()
        {
            return await this.SendAsync<StatusResponse>("/api/v2/status");
        }

        public async Task<QueryResponse> QueryAsync(Principal canisterId, string method, CandidArg encodedArgument, IIdentity? identityOverride = null)
        {
            return await this.SendAsync<QueryRequest, QueryResponse>($"/api/v2/canister/{canisterId.ToText()}/query", BuildRequest, identityOverride);

            QueryRequest BuildRequest(Principal sender, ICTimestamp now)
            {
                return new QueryRequest(canisterId, method, encodedArgument, sender, now);
            }
        }

        public async Task<ReadStateResponse> ReadStateAsync(Principal canisterId, List<PathSegment> path, IIdentity? identityOverride)
        {
            return await this.SendAsync<ReadStateRequest, ReadStateResponse>($"/api/v2/canister/{canisterId.ToText()}/read_state", BuildRequest, identityOverride);

            ReadStateRequest BuildRequest(Principal sender, ICTimestamp now)
            {
                return new ReadStateRequest(path, sender, now);
            }
        }


        private async Task SendWithNoResponseAsync<TRequest>(string url, Func<Principal, ICTimestamp, TRequest> getRequest, IIdentity? identityOverride)
            where TRequest : IRepresentationIndependentHashItem
        {
            _ = await this.SendInternalAsync(url, getRequest, identityOverride);
        }

        private async Task<TResponse> SendAsync<TResponse>(string url)
        {
            Func<Task<Stream>> streamFunc = await this.SendRawAsync(url, null);
            Stream stream = await streamFunc();
            return await Dahomey.Cbor.Cbor.DeserializeAsync<TResponse>(stream, HttpAgent.cborOptionsLazy.Value);
        }

        private async Task<TResponse> SendAsync<TRequest, TResponse>(string url, Func<Principal, ICTimestamp, TRequest> getRequest, IIdentity? identityOverride)
            where TRequest : IRepresentationIndependentHashItem
        {
            Func<Task<Stream>> streamFunc = await this.SendInternalAsync(url, getRequest, identityOverride);
            Stream stream = await streamFunc();
#if DEBUG
            string cborHex;
            using (var memoryStream = new MemoryStream())
            {
                stream.CopyTo(memoryStream);
                byte[] cborBytes = memoryStream.ToArray();
                cborHex = ByteUtil.ToHexString(cborBytes);
            }
            stream.Position = 0;
#endif
            return await Dahomey.Cbor.Cbor.DeserializeAsync<TResponse>(stream, HttpAgent.cborOptionsLazy.Value);
        }

        private async Task<Func<Task<Stream>>> SendInternalAsync<TRequest>(string url, Func<Principal, ICTimestamp, TRequest> getRequest, IIdentity? identityOverride)
            where TRequest : IRepresentationIndependentHashItem
        {
            if (identityOverride == null)
            {
                identityOverride = this.Identity;
            }
            TRequest request = getRequest(identityOverride.Principal, ICTimestamp.Now());
            Dictionary<string, IHashable> content = request.BuildHashableItem();
            SignedContent signedContent = identityOverride.CreateSignedContent(content);

            using (var stream = new MemoryStream())
            {
                await Dahomey.Cbor.Cbor.SerializeAsync(signedContent, stream, HttpAgent.cborOptionsLazy.Value);
                byte[] cborBody = stream.ToArray();
                return await this.SendRawAsync(url, cborBody);
            }
        }


        private async Task<Func<Task<Stream>>> SendRawAsync(string url, byte[]? cborBody = null)
        {
            HttpRequestMessage request;
            if (cborBody != null)
            {
                var content = new ByteArrayContent(cborBody);
                content.Headers.Remove("Content-Type");
                content.Headers.Add("Content-Type", CBOR_CONTENT_TYPE);
                request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = content
                };
            }
            else
            {
                request = new HttpRequestMessage(HttpMethod.Get, url);

            }
            HttpResponseMessage response = await this.httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                string message = Encoding.UTF8.GetString(bytes);
                throw new Exception($"Response returned a failed status. HttpStatusCode={response.StatusCode} Reason={response.ReasonPhrase} Message={message}");
            }
            return async () => await response.Content.ReadAsStreamAsync();
        }
    }
}


