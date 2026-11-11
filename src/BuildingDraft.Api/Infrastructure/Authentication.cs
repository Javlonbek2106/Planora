using System.Text;
using BuildingDraft.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace BuildingDraft.Api.Infrastructure;

public static class Authentication
{
    public const string UserIdItem = "BuildingDraft.UserId";

    // Dev mode (one fixed user, no tokens) exists only for local development and refuses to start elsewhere.
    public static bool IsDevMode(this IConfiguration configuration) =>
        string.Equals(configuration["Auth:Mode"], "Dev", StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (configuration.IsDevMode())
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Auth:Mode=Dev is only allowed in the Development environment.");
            return services.AddScoped<ICurrentUser, DevCurrentUser>();
        }

        var authority = configuration["Auth:Authority"];
        var signingKey = configuration["Auth:SigningKey"];
        if (string.IsNullOrWhiteSpace(authority) && string.IsNullOrWhiteSpace(signingKey))
            throw new InvalidOperationException("Configure Auth:Authority (OpenID Connect) or Auth:SigningKey (shared secret) for Jwt mode.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.Audience = configuration["Auth:Audience"];
            if (!string.IsNullOrWhiteSpace(authority))
                options.Authority = authority;
            else
                options.TokenValidationParameters = new()
                {
                    ValidateIssuer = !string.IsNullOrWhiteSpace(configuration["Auth:Issuer"]),
                    ValidIssuer = configuration["Auth:Issuer"],
                    ValidateAudience = true,
                    ValidAudience = configuration["Auth:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey!)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
        });
        return services.AddAuthorization().AddHttpContextAccessor().AddScoped<ICurrentUser, HttpCurrentUser>();
    }
}

// The user id resolved by UserProvisioningMiddleware for this request.
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid UserId => accessor.HttpContext?.Items[Authentication.UserIdItem] is Guid id
        ? id
        : throw new InvalidOperationException("No authenticated user for this request.");
}

// Runs after authorization, so only authenticated requests reach it. It uses its own scope (and database
// context) so a failed first-login insert can never leak into the request's unit of work.
public sealed class UserProvisioningMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IServiceScopeFactory scopes)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subject = context.User.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(subject))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var scope = scopes.CreateScope();
            context.Items[Authentication.UserIdItem] = await scope.ServiceProvider.GetRequiredService<UserService>()
                .ResolveAsync(subject, context.User.FindFirst("email")?.Value, context.User.FindFirst("name")?.Value, context.RequestAborted);
        }

        await next(context);
    }
}
