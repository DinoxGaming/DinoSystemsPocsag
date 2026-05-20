using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var apiKey = app.Configuration["POCSAG_API_KEY"] ?? "dev-key";
var messages = new ConcurrentQueue<PocsagMessage>();

app.MapGet("/", () => Results.Content(Pages.IndexHtml, "text/html"));

app.MapGet("/api/messages", () =>
{
    return Results.Ok(messages.Reverse().Take(200));
});

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

    while (messages.Count > 1000 && messages.TryDequeue(out _))
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

internal static class Pages
{
    public const string IndexHtml = """
    <!doctype html>
    <html lang="nl">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>POCSAG Meldingen</title>
      <style>
        body { margin: 0; font-family: Segoe UI, Arial, sans-serif; background: #f4f6f8; color: #17202a; }
        header { padding: 18px 24px; background: #122033; color: white; }
        h1 { margin: 0; font-size: 22px; font-weight: 650; }
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
          header { padding: 15px 16px; }
          h1 { font-size: 20px; }
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
        <h1>POCSAG Meldingen</h1>
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

        async function refresh() {
          const response = await fetch('/api/messages', { cache: 'no-store' });
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
