using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "pocsag_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.LoginPath = "/login";
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

var apiKey = app.Configuration["POCSAG_API_KEY"] ?? "dev-key";
var messages = new ConcurrentQueue<PocsagMessage>();

app.MapGet("/login", () => Results.Content(Pages.LoginHtml(), "text/html"));

app.MapPost("/login", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var password = form["password"].ToString();

    if (!PasswordVerifier.Verify(password))
    {
        return Results.Content(Pages.LoginHtml("Wachtwoord klopt niet."), "text/html", statusCode: StatusCodes.Status401Unauthorized);
    }

    var claims = new[] { new Claim(ClaimTypes.Name, "viewer") };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    return Results.Redirect("/");
});

app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();

app.MapGet("/", () => Results.Content(Pages.MessagesHtml("POCSAG Meldingen", "/api/messages"), "text/html"))
    .RequireAuthorization();

app.MapGet("/prorail", () => Results.Content(Pages.MessagesHtml("ProRail Meldingen", "/api/messages/prorail"), "text/html"))
    .RequireAuthorization();

app.MapGet("/api/messages", () =>
{
    return Results.Ok(messages.Reverse().Take(1000));
}).RequireAuthorization();

app.MapGet("/api/messages/prorail", () =>
{
    var prorailMessages = messages
        .Reverse()
        .Where(message => message.Text.Contains("icb", StringComparison.OrdinalIgnoreCase))
        .Take(1000);

    return Results.Ok(prorailMessages);
}).RequireAuthorization();

app.MapPost("/api/messages", (PocsagMessageInput input, HttpRequest request) =>
{
    if (!request.Headers.TryGetValue("X-Api-Key", out var providedKey) || providedKey != apiKey)
    {
        return Results.Unauthorized();
    }

    var message = new PocsagMessage(
        DateTimeOffset.UtcNow,
        input.Address,
        input.Function,
        input.BaudRate,
        string.IsNullOrWhiteSpace(input.Type) ? "UNKNOWN" : input.Type.Trim(),
        string.IsNullOrWhiteSpace(input.Text) ? "" : input.Text.Trim());

    messages.Enqueue(message);

    while (messages.Count > 3500 && messages.TryDequeue(out _))
    {
    }

    return Results.Created("/api/messages", message);
});

app.Run();

public sealed record PocsagMessage(
    DateTimeOffset ReceivedAt,
    int Address,
    int Function,
    int BaudRate,
    string Type,
    string Text);

public sealed record PocsagMessageInput(
    int Address,
    int Function,
    int BaudRate,
    string Type,
    string Text);

internal static class PasswordVerifier
{
    private const int Iterations = 210000;
    private const int KeySize = 32;

    // PBKDF2-HMAC-SHA256 hash for the configured site password.
    private const string SaltBase64 = "0IgyIAluUb7sMGE1eyZCgw==";
    private const string HashBase64 = "9wgikNN+qkuEDW7kzGVfjDhG6n4iD61k1nQ4VVeSat8=";

    public static bool Verify(string password)
    {
        var salt = Convert.FromBase64String(SaltBase64);
        var expectedHash = Convert.FromBase64String(HashBase64);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}

internal static class Pages
{
    public static string LoginHtml(string? error = null)
    {
        var errorHtml = string.IsNullOrWhiteSpace(error)
            ? ""
            : $"""<p class="error">{EscapeHtml(error)}</p>""";

        return $$"""
        <!doctype html>
        <html lang="nl">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Inloggen</title>
          <style>
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; font-family: Segoe UI, Arial, sans-serif; background: #f4f6f8; color: #17202a; }
            form { width: min(360px, calc(100vw - 32px)); background: white; border: 1px solid #d9e0e8; padding: 22px; box-sizing: border-box; }
            h1 { margin: 0 0 16px; font-size: 22px; }
            label { display: block; margin-bottom: 6px; color: #526070; font-size: 13px; }
            input { width: 100%; height: 40px; padding: 8px 10px; border: 1px solid #bac5d1; box-sizing: border-box; font-size: 16px; }
            button { width: 100%; height: 40px; margin-top: 14px; border: 0; background: #122033; color: white; font-size: 15px; font-weight: 650; cursor: pointer; }
            .error { margin: 0 0 12px; color: #b42318; font-size: 14px; }
          </style>
        </head>
        <body>
          <form method="post" action="/login">
            <h1>POCSAG Meldingen</h1>
            {{errorHtml}}
            <label for="password">Wachtwoord</label>
            <input id="password" name="password" type="password" autocomplete="current-password" autofocus>
            <button type="submit">Openen</button>
          </form>
        </body>
        </html>
        """;
    }

    public static string MessagesHtml(string title, string apiPath)
    {
        return $$"""
        <!doctype html>
        <html lang="nl">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{EscapeHtml(title)}}</title>
          <style>
            body { margin: 0; font-family: Segoe UI, Arial, sans-serif; background: #f4f6f8; color: #17202a; }
            header { display: flex; align-items: center; justify-content: space-between; gap: 14px; padding: 18px 24px; background: #122033; color: white; }
            h1 { margin: 0; font-size: 22px; font-weight: 650; }
            nav { display: flex; gap: 12px; align-items: center; }
            a, .logout { color: white; font-size: 14px; background: transparent; border: 0; text-decoration: none; cursor: pointer; padding: 0; }
            main { padding: 18px 24px; }
            table { width: 100%; border-collapse: collapse; background: white; border: 1px solid #d9e0e8; }
            th, td { padding: 11px 12px; border-bottom: 1px solid #e5eaf0; text-align: left; vertical-align: top; }
            th { font-size: 13px; color: #526070; background: #f9fafb; }
            td { font-size: 15px; }
            .time { width: 72px; white-space: nowrap; color: #526070; font-variant-numeric: tabular-nums; }
            .address { width: 105px; white-space: nowrap; font-weight: 650; font-variant-numeric: tabular-nums; }
            .muted { color: #526070; }
            .text { white-space: pre-wrap; word-break: break-word; }
            @media (max-width: 640px) {
              header { align-items: flex-start; flex-direction: column; padding: 15px 16px; }
              h1 { font-size: 20px; }
              nav { width: 100%; justify-content: space-between; }
              main { padding: 12px; }
              table, tbody, tr, td { display: block; width: 100%; box-sizing: border-box; }
              table { border: 0; background: transparent; }
              thead { display: none; }
              tr { margin: 0 0 10px; border: 1px solid #d9e0e8; background: white; }
              td { border-bottom: 0; padding: 6px 10px; }
              td:first-child { padding-top: 10px; }
              td:last-child { padding-bottom: 11px; }
              .time, .address { display: inline-block; width: auto; }
              .time { margin-right: 10px; font-size: 13px; }
              .address { font-size: 13px; }
              .text { margin-top: 4px; font-size: 16px; line-height: 1.35; }
            }
          </style>
        </head>
        <body>
          <header>
            <h1>{{EscapeHtml(title)}}</h1>
            <nav>
              <a href="/">Alle meldingen</a>
              <a href="/prorail">ProRail</a>
              <form method="post" action="/logout"><button class="logout" type="submit">Uitloggen</button></form>
            </nav>
          </header>
          <main>
            <table>
              <thead>
                <tr>
                  <th>Tijd</th>
                  <th>Capcode</th>
                  <th>Bericht</th>
                </tr>
              </thead>
              <tbody id="rows">
                <tr><td colspan="3" class="muted">Laden...</td></tr>
              </tbody>
            </table>
          </main>
          <script>
            const rows = document.getElementById('rows');
            const apiPath = '{{apiPath}}';

            async function refresh() {
              const response = await fetch(apiPath, { cache: 'no-store' });
              if (response.status === 401) {
                window.location.href = '/login';
                return;
              }

              const messages = await response.json();

              if (!messages.length) {
                rows.innerHTML = '<tr><td colspan="3" class="muted">Nog geen meldingen ontvangen.</td></tr>';
                return;
              }

              rows.innerHTML = messages.map(message => {
                const receivedAt = new Date(message.receivedAt).toLocaleTimeString('nl-NL', {
                  hour: '2-digit',
                  minute: '2-digit'
                });
                const address = String(message.address).padStart(7, '0');
                return `<tr>
                  <td class="time">${escapeHtml(receivedAt)}</td>
                  <td class="address">${escapeHtml(address)}</td>
                  <td class="text">${escapeHtml(message.text)}</td>
                </tr>`;
              }).join('');
            }

            function escapeHtml(value) {
              return String(value).replace(/[&<>"']/g, char => ({
                '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;'
              }[char]));
            }

            refresh();
            setInterval(refresh, 3000);
          </script>
        </body>
        </html>
        """;
    }

    private static string EscapeHtml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#039;");
    }
}
