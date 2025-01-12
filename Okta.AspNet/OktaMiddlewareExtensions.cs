﻿// <copyright file="OktaMiddlewareExtensions.cs" company="Okta, Inc">
// Copyright (c) 2018-present Okta, Inc. All rights reserved.
// Licensed under the Apache 2.0 license. See the LICENSE file in the project root for full license information.
// </copyright>

using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Jwt;
using Microsoft.Owin.Security.Notifications;
using Microsoft.Owin.Security.OpenIdConnect;
using Okta.AspNet.Abstractions;
using Owin;

namespace Okta.AspNet
{
    public static class OktaMiddlewareExtensions
    {
        public static IAppBuilder UseOktaMvc(this IAppBuilder app, OktaMvcOptions options)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }

            new OktaMvcOptionsValidator().Validate(options);
            AddOpenIdConnectAuthentication(app, options);

            return app;
        }

        public static IAppBuilder UseOktaWebApi(this IAppBuilder app, OktaWebApiOptions options)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }

            new OktaWebApiOptionsValidator().Validate(options);
            AddJwtBearerAuthentication(app, options);

            return app;
        }

        private static void AddJwtBearerAuthentication(IAppBuilder app, OktaWebApiOptions options)
        {
            var issuer = UrlHelper.CreateIssuerUrl(options.OktaDomain, options.AuthorizationServerId);
            var httpClient = new HttpClient(new UserAgentHandler("okta-aspnet", typeof(OktaMiddlewareExtensions).Assembly.GetName().Version));

            var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
              issuer + "/.well-known/openid-configuration",
              new OpenIdConnectConfigurationRetriever(),
              new HttpDocumentRetriever(httpClient));

            // Stop the default behavior of remapping JWT claim names to legacy MS/SOAP claim names
            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

            var signingKeyProvider = new DiscoveryDocumentSigningKeyProvider(configurationManager);
            var tokenValidationParameters = new DefaultTokenValidationParameters(options, issuer)
            {
                ValidAudience = options.Audience,
                IssuerSigningKeyResolver = (token, securityToken, keyId, validationParameters) =>
                {
                    var signingKeys = signingKeyProvider.GetSigningKeysAsync().GetAwaiter().GetResult();
                    return signingKeys.Where(x => x.KeyId == keyId);
                },
                // CLIST: 2019-11-08 - save the claims token into the bootstrap context for k2
                SaveSigninToken = true,
            };

            app.UseJwtBearerAuthentication(new JwtBearerAuthenticationOptions
            {
                AuthenticationMode = AuthenticationMode.Active,
                TokenValidationParameters = tokenValidationParameters,
                TokenHandler = new StrictTokenHandler(),
            });
        }

        private static void AddOpenIdConnectAuthentication(IAppBuilder app, OktaMvcOptions options)
        {
            // Stop the default behavior of remapping JWT claim names to legacy MS/SOAP claim names
            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

            var notifications = new OpenIdConnectAuthenticationNotifications
            {
                RedirectToIdentityProvider = BeforeRedirectToIdentityProviderAsync,
            };

            app.UseOpenIdConnectAuthentication(OpenIdConnectAuthenticationOptionsBuilder.BuildOpenIdConnectAuthenticationOptions(options, notifications));
        }

        private static Task BeforeRedirectToIdentityProviderAsync(RedirectToIdentityProviderNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions> redirectToIdentityProviderNotification)
        {
            // If signing out, add the id_token_hint
            if (redirectToIdentityProviderNotification.ProtocolMessage.RequestType == OpenIdConnectRequestType.Logout)
            {
                if (redirectToIdentityProviderNotification.OwinContext.Authentication.User.FindFirst("id_token") != null)
                {
                    redirectToIdentityProviderNotification.ProtocolMessage.IdTokenHint = redirectToIdentityProviderNotification.OwinContext.Authentication.User.FindFirst("id_token").Value;
                }
            }

            // Add sessionToken to provide custom login
            if (redirectToIdentityProviderNotification.ProtocolMessage.RequestType == OpenIdConnectRequestType.Authentication)
            {
                var sessionToken = string.Empty;
                redirectToIdentityProviderNotification.OwinContext.Authentication.AuthenticationResponseChallenge?.Properties?.Dictionary?.TryGetValue("sessionToken", out sessionToken);

                if (!string.IsNullOrEmpty(sessionToken))
                {
                    redirectToIdentityProviderNotification.ProtocolMessage.SetParameter("sessionToken", sessionToken);
                }

                var idpId = string.Empty;
                redirectToIdentityProviderNotification.OwinContext.Authentication.AuthenticationResponseChallenge?.Properties?.Dictionary?.TryGetValue("idp", out idpId);

                if (!string.IsNullOrEmpty(idpId))
                {
                    redirectToIdentityProviderNotification.ProtocolMessage.SetParameter("idp", idpId);
                }
            }

            return Task.FromResult(false);
        }
    }
}
