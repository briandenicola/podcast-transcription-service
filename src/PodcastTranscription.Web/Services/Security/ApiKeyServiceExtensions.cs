using Microsoft.AspNetCore.Authentication;
using PodcastTranscription.Web.Endpoints;

namespace PodcastTranscription.Web.Services.Security;

public static class ApiKeyServiceExtensions
{
    public static IServiceCollection AddApiKeyAccess(this IServiceCollection services)
    {
        services.AddScoped<ApiKeyStore>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(ApiKeyEndpoints.ManagementPolicy, policy => policy
                .AddAuthenticationSchemes(AdminAuthenticator.CookieScheme)
                .RequireAuthenticatedUser())
            .AddPolicy(ApiKeyAuthenticationHandler.ReadPolicy, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim(ApiKeyAuthenticationHandler.PermissionClaim, "read"))
            .AddPolicy(ApiKeyAuthenticationHandler.SubmitPolicy, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(Roles.MemberOrAbove)
                .RequireClaim(ApiKeyAuthenticationHandler.PermissionClaim, "submit"));
        return services;
    }
}
