using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PodcastTranscription.Web.Services.Security;

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyStore keys,
    AdminAuthenticator admin) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string ReadPolicy = "ApiRead";
    public const string SubmitPolicy = "ApiSubmit";
    public const string PermissionClaim = "api_permission";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AdminAuthenticator.ApiKeyHeader, out var values))
        {
            return AuthenticateResult.NoResult();
        }
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return AuthenticateResult.Fail("Missing or invalid API key.");
        }

        var secret = values[0]!;
        ClaimsPrincipal? principal;
        if (admin.ValidateApiKey(secret))
        {
            // Preserve the shared key's existing access without attributing its work to a user.
            principal = new(new ClaimsIdentity(
                [new Claim(PermissionClaim, "read"), new Claim(PermissionClaim, "submit"),
                 new Claim(ClaimTypes.Role, Roles.Member)], SchemeName));
        }
        else
        {
            principal = await keys.AuthenticateAsync(secret, Context.RequestAborted);
        }

        return principal is null
            ? AuthenticateResult.Fail("Missing or invalid API key.")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status401Unauthorized, "Missing or invalid API key.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status403Forbidden, "This API key does not permit episode submission.");

    private Task WriteProblemAsync(int status, string detail) =>
        Results.Problem(detail, statusCode: status).ExecuteAsync(Context);
}
